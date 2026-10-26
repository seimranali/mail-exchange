using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Imap;
using MX.MailServer.Storage;
using MX.MailServer.Submission;
using MX.Persistence;

namespace MX.MailServer.Webmail;

public sealed record WebmailProfile(string Address, string DisplayName, IReadOnlyList<string> Aliases, long QuotaBytes, long UsedBytes);
public sealed record WebmailFolder(MailFolder Folder, int Total, int Unread);
public sealed record WebmailAddress(string? Name, string Address);
public sealed record WebmailListItem(Guid Id, MailFolder Folder, WebmailAddress? From, IReadOnlyList<WebmailAddress> To, string? Subject, string Snippet,
    DateTimeOffset Date, long Size, bool IsRead, bool IsFlagged, bool IsAnswered, bool HasAttachments);
public sealed record WebmailList(IReadOnlyList<WebmailListItem> Items, int Total, int Page, int PageSize);
public sealed record WebmailAttachment(int Index, string FileName, string ContentType, long Size);
public sealed record WebmailMessage(Guid Id, MailFolder Folder, WebmailAddress? From, IReadOnlyList<WebmailAddress> To, IReadOnlyList<WebmailAddress> Cc,
    IReadOnlyList<WebmailAddress> ReplyTo, string? Subject, DateTimeOffset Date, string? Html, string? Text, IReadOnlyList<WebmailAttachment> Attachments,
    bool IsRead, bool IsFlagged, bool IsAnswered, string? MessageId, bool ReceivedOverTls, AuthCheckResult Spf, AuthCheckResult Dkim, AuthCheckResult Dmarc, decimal? SpamScore);
public sealed record WebmailUpload(string FileName, string ContentType, byte[] Content);
public sealed record WebmailSendRequest(string To, string? Cc, string? Bcc, string? Subject, string? Text, string? Html, Guid? InReplyTo, string? From, IReadOnlyList<WebmailUpload> Attachments);

/// <summary>Raised for a webmail request the user can fix (bad address, quota, rejected send).</summary>
public sealed class WebmailException(string message) : Exception(message);

/// <summary>
/// Mail access for a signed-in webmail user. Like IMAP, <b>every query filters by MailboxId</b>:
/// RLS separates organizations, but colleagues must not see each other's mail.
/// </summary>
public sealed partial class WebmailService(IServiceScopeFactory scopes, IMessageStore store, ISubmissionHandler submission, IMemoryCache cache, IOptions<MailServerOptions> options)
{
    private const int SnippetPrefixBytes = 64 * 1024;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"cid:([^""'\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidReference();

    private async Task<T> WithDbAsync<T>(AuthenticatedMailbox user, Func<MxDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(user.OrganizationId, null);
        return await work(scope.ServiceProvider.GetRequiredService<MxDbContext>());
    }

    private static IQueryable<MailMessage> Mine(MxDbContext db, AuthenticatedMailbox user) =>
        db.MailMessages.Where(m => m.MailboxId == user.MailboxId && !m.IsDeleted);

    public Task<WebmailProfile> ProfileAsync(AuthenticatedMailbox user, CancellationToken ct) => WithDbAsync(user, async db =>
    {
        var m = await db.Mailboxes.AsNoTracking().Where(x => x.Id == user.MailboxId).Select(x => new { x.EmailAddress, x.DisplayName, x.QuotaBytes, x.UsedBytes }).SingleAsync(ct);
        var aliases = await db.MailAliases.AsNoTracking().Where(a => a.MailboxId == user.MailboxId).OrderBy(a => a.AliasAddress).Select(a => a.AliasAddress).ToListAsync(ct);
        return new WebmailProfile(m.EmailAddress, m.DisplayName, aliases, m.QuotaBytes, m.UsedBytes);
    });

    public Task<List<WebmailFolder>> FoldersAsync(AuthenticatedMailbox user, CancellationToken ct) => WithDbAsync(user, async db =>
    {
        var counts = await Mine(db, user).GroupBy(m => m.Folder)
            .Select(g => new { Folder = g.Key, Total = g.Count(), Unread = g.Count(m => !m.IsRead) }).ToListAsync(ct);
        return ImapMailStore.Folders.Keys
            .Select(f => counts.FirstOrDefault(c => c.Folder == f) is { } c ? new WebmailFolder(f, c.Total, c.Unread) : new WebmailFolder(f, 0, 0))
            .ToList();
    });

    public Task<WebmailList> ListAsync(AuthenticatedMailbox user, MailFolder folder, string? search, bool unreadOnly, int page, int pageSize, CancellationToken ct) => WithDbAsync(user, async db =>
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var query = Mine(db, user).AsNoTracking().Where(m => m.Folder == folder);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(m => EF.Functions.ILike(m.Subject ?? "", pattern) || EF.Functions.ILike(m.FromHeader ?? "", pattern) || EF.Functions.ILike(m.EnvelopeFrom, pattern));
        }
        if (unreadOnly) query = query.Where(m => !m.IsRead);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(m => m.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(m => new { m.Id, m.Folder, m.FromHeader, m.Subject, m.ReceivedAt, m.SizeBytes, m.IsRead, m.IsFlagged, m.IsAnswered, m.StoragePath })
            .ToListAsync(ct);

        var items = new List<WebmailListItem>(rows.Count);
        foreach (var r in rows)
        {
            var summary = await SummaryAsync(r.Id, r.StoragePath, ct);
            items.Add(new WebmailListItem(r.Id, r.Folder, summary.From ?? ParseFirst(r.FromHeader), summary.To, r.Subject, summary.Snippet,
                r.ReceivedAt, r.SizeBytes, r.IsRead, r.IsFlagged, r.IsAnswered, summary.HasAttachments));
        }
        return new WebmailList(items, total, page, pageSize);
    });

    private sealed record Summary(WebmailAddress? From, IReadOnlyList<WebmailAddress> To, string Snippet, bool HasAttachments);

    /// <summary>From/To, a text preview and an attachment hint, parsed from the start of the file. Cached: messages never change.</summary>
    private async Task<Summary> SummaryAsync(Guid id, string path, CancellationToken ct)
    {
        if (cache.TryGetValue(("webmail-summary", id), out Summary? cached)) return cached!;
        var summary = new Summary(null, [], "", false);
        try
        {
            var buffer = new byte[SnippetPrefixBytes];
            int read;
            await using (var file = store.OpenRead(path)) read = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            var message = await MimeMessage.LoadAsync(new MemoryStream(buffer, 0, read, writable: false), ct);
            var text = message.TextBody ?? HtmlToText(message.HtmlBody) ?? "";
            var snippet = Whitespace().Replace(text, " ").Trim();
            var hasAttachments = message.Attachments.Any() || message.Headers[HeaderId.ContentType]?.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase) == true;
            summary = new Summary(Addresses(message.From).FirstOrDefault(), Addresses(message.To), snippet.Length > 160 ? snippet[..160] : snippet, hasAttachments);
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException)
        {
            // Unreadable or missing file: list it with what the index knows.
        }
        cache.Set(("webmail-summary", id), summary, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(1), Size = 1 });
        return summary;
    }

    public async Task<WebmailMessage> GetAsync(AuthenticatedMailbox user, Guid id, CancellationToken ct)
    {
        var row = await WithDbAsync(user, db => Mine(db, user).FirstOrDefaultAsync(m => m.Id == id, ct)) ?? throw new NotFoundException("Message", id);
        var message = await LoadAsync(row.StoragePath, ct);
        if (!row.IsRead)
        {
            await WithDbAsync(user, db => Mine(db, user).Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true).SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct));
            row.IsRead = true;
        }

        var attachments = Attachments(message).Select((a, i) => new WebmailAttachment(i, FileName(a, i), a.ContentType.MimeType, EstimateSize(a))).ToList();
        return new WebmailMessage(row.Id, row.Folder, Addresses(message.From).FirstOrDefault(), Addresses(message.To), Addresses(message.Cc), Addresses(message.ReplyTo),
            message.Subject, message.Date != DateTimeOffset.MinValue ? message.Date : row.ReceivedAt, InlineImages(message), message.TextBody, attachments,
            row.IsRead, row.IsFlagged, row.IsAnswered, message.MessageId, row.ReceivedOverTls, row.SpfResult, row.DkimResult, row.DmarcResult, row.SpamScore);
    }

    public async Task<(string FileName, string ContentType, byte[] Content)> GetAttachmentAsync(AuthenticatedMailbox user, Guid id, int index, CancellationToken ct)
    {
        var path = await WithDbAsync(user, db => Mine(db, user).Where(m => m.Id == id).Select(m => m.StoragePath).FirstOrDefaultAsync(ct)) ?? throw new NotFoundException("Message", id);
        var message = await LoadAsync(path, ct);
        var part = Attachments(message).ElementAtOrDefault(index) ?? throw new NotFoundException("Attachment", index);
        using var buffer = new MemoryStream();
        if (part is MimePart { Content: { } body }) await body.DecodeToAsync(buffer, ct);
        else if (part is MessagePart { Message: { } attached }) await attached.WriteToAsync(buffer, ct);
        return (FileName(part, index), part.ContentType.MimeType, buffer.ToArray());
    }

    public Task UpdateFlagsAsync(AuthenticatedMailbox user, IReadOnlyList<Guid> ids, bool? read, bool? flagged, CancellationToken ct) => WithDbAsync(user, async db =>
    {
        var query = Mine(db, user).Where(m => ids.Contains(m.Id));
        if (read is { } r) await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, r).SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (flagged is { } f) await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsFlagged, f).SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct);
        return 0;
    });

    /// <summary>Moves messages to another folder with fresh IMAP UIDs, so mail apps see the change.</summary>
    public Task MoveAsync(AuthenticatedMailbox user, IReadOnlyList<Guid> ids, MailFolder target, CancellationToken ct) => WithDbAsync(user, async db =>
    {
        var toMove = await Mine(db, user).Where(m => ids.Contains(m.Id) && m.Folder != target).OrderBy(m => m.ReceivedAt).Select(m => m.Id).ToListAsync(ct);
        if (toMove.Count == 0) return 0;
        var firstUid = await UidAllocator.AllocateAsync(db, user.OrganizationId, user.MailboxId, target, toMove.Count, ct);
        for (var i = 0; i < toMove.Count; i++)
        {
            var id = toMove[i];
            var uid = firstUid + i;
            await db.MailMessages.Where(m => m.MailboxId == user.MailboxId && m.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Folder, target).SetProperty(m => m.Uid, uid).SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
        return toMove.Count;
    });

    /// <summary>Deleting moves to Trash; deleting from Trash (or Junk) removes the message for good.</summary>
    public Task DeleteAsync(AuthenticatedMailbox user, IReadOnlyList<Guid> ids, CancellationToken ct) => WithDbAsync(user, async db =>
    {
        var rows = await Mine(db, user).Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Folder, m.StoragePath, m.SizeBytes }).ToListAsync(ct);
        var permanent = rows.Where(r => r.Folder is MailFolder.Trash or MailFolder.Junk).ToList();
        var toTrash = rows.Except(permanent).Select(r => r.Id).ToList();
        if (toTrash.Count > 0) await MoveAsync(user, toTrash, MailFolder.Trash, ct);
        if (permanent.Count > 0)
        {
            if (await db.LegalHolds.AnyAsync(h => h.Status == LegalHoldStatus.Active && h.MailboxIds.Contains(user.MailboxId), ct))
                throw new WebmailException("This mailbox is under legal hold; messages cannot be permanently deleted.");
            var permanentIds = permanent.Select(p => p.Id).ToList();
            await db.MailMessages.Where(m => m.MailboxId == user.MailboxId && permanentIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
            var freed = permanent.Sum(p => p.SizeBytes);
            await db.Mailboxes.Where(m => m.Id == user.MailboxId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.UsedBytes, m => m.UsedBytes - freed < 0 ? 0 : m.UsedBytes - freed), ct);
            foreach (var p in permanent) store.Delete(p.StoragePath);
        }
        return 0;
    });

    public async Task<string> SendAsync(AuthenticatedMailbox user, WebmailSendRequest request, CancellationToken ct)
    {
        var to = ParseList(request.To, "To");
        var cc = ParseList(request.Cc, "Cc");
        var bcc = ParseList(request.Bcc, "Bcc");
        var recipients = to.Concat(cc).Concat(bcc).Select(a => a.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Count == 0) throw new WebmailException("Add at least one recipient.");
        if (recipients.Count > options.Value.MaxRecipients) throw new WebmailException($"A message can have at most {options.Value.MaxRecipients} recipients.");

        var (fromAddress, displayName, original) = await WithDbAsync(user, async db =>
        {
            var name = await db.Mailboxes.AsNoTracking().Where(m => m.Id == user.MailboxId && m.Status == MailboxStatus.Active && m.PasswordHash != null)
                .Select(m => m.DisplayName).SingleOrDefaultAsync(ct) ?? throw new WebmailException("This mailbox is disabled.");
            var aliases = await db.MailAliases.AsNoTracking().Where(a => a.MailboxId == user.MailboxId).Select(a => a.AliasAddress).ToListAsync(ct);
            var from = string.IsNullOrWhiteSpace(request.From) ? user.Address : request.From.Trim().ToLowerInvariant();
            if (from != user.Address && !aliases.Contains(from)) throw new WebmailException($"You cannot send as {from}.");
            var orig = request.InReplyTo is { } replyId ? await Mine(db, user).AsNoTracking().FirstOrDefaultAsync(m => m.Id == replyId, ct) : null;
            return (from, name, orig);
        });
        var sender = user with { AllowedSenders = new HashSet<string>([fromAddress], StringComparer.OrdinalIgnoreCase) };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(displayName, fromAddress));
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Subject = request.Subject?.Trim() ?? "";
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(fromAddress.Split('@')[1]);
        if (original?.InternetMessageId is { } parentId)
        {
            var bare = parentId.Trim('<', '>', ' ');
            message.InReplyTo = bare;
            message.References.Add(bare);
        }

        var body = new BodyBuilder { TextBody = request.Text ?? HtmlToText(request.Html) ?? "" };
        if (!string.IsNullOrWhiteSpace(request.Html)) body.HtmlBody = request.Html;
        long attachmentBytes = 0;
        foreach (var file in request.Attachments)
        {
            attachmentBytes += file.Content.LongLength;
            body.Attachments.Add(string.IsNullOrWhiteSpace(file.FileName) ? "attachment" : Path.GetFileName(file.FileName), file.Content,
                ContentType.TryParse(file.ContentType, out var type) ? type : new ContentType("application", "octet-stream"));
        }
        if (attachmentBytes > options.Value.MaxMessageSizeBytes * 3 / 4) throw new WebmailException("Attachments are too large.");
        message.Body = body.ToMessageBody();

        byte[] content;
        using (var buffer = new MemoryStream())
        {
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            await message.WriteToAsync(format, buffer, ct);
            content = buffer.ToArray();
        }
        if (content.LongLength > options.Value.MaxMessageSizeBytes) throw new WebmailException("The message is too large to send.");

        var result = await submission.SubmitAsync(sender, fromAddress, recipients, content, ct);
        if (!result.Accepted) throw new WebmailException(result.Reply.Length > 4 ? result.Reply[4..] : result.Reply);
        if (original is not null)
            await WithDbAsync(user, db => Mine(db, user).Where(m => m.Id == original.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsAnswered, true), ct));
        return message.MessageId;
    }

    private async Task<MimeMessage> LoadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var file = store.OpenRead(path);
            return await MimeMessage.LoadAsync(file, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new WebmailException("The message file is missing from the store.");
        }
    }

    private static List<MimeEntity> Attachments(MimeMessage message) =>
        message.BodyParts.Where(p => p.IsAttachment || (p is MimePart mp && mp.ContentId is null && mp.FileName is not null && p is not TextPart)).Distinct().ToList();

    private static string FileName(MimeEntity part, int index) => part switch
    {
        MimePart { FileName: { Length: > 0 } name } => name,
        MessagePart m => $"{m.Message?.Subject ?? "message"}.eml",
        _ => $"attachment-{index + 1}",
    };

    private static long EstimateSize(MimeEntity part) => part is MimePart { Content: { } c } && c.Stream.CanSeek
        ? c.Encoding == ContentEncoding.Base64 ? c.Stream.Length * 3 / 4 : c.Stream.Length
        : 0;

    /// <summary>HTML body with cid: images inlined as data URIs (the viewer has no other way to fetch them).</summary>
    private static string? InlineImages(MimeMessage message)
    {
        var html = message.HtmlBody;
        if (html is null) return null;
        var related = message.BodyParts.OfType<MimePart>().Where(p => p.ContentId is not null).ToDictionary(p => p.ContentId!.Trim('<', '>'), p => p, StringComparer.OrdinalIgnoreCase);
        if (related.Count == 0) return html;
        return CidReference().Replace(html, m =>
        {
            if (!related.TryGetValue(m.Groups[1].Value, out var part) || part.Content is null) return m.Value;
            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            if (buffer.Length > 3 * 1024 * 1024) return m.Value;
            return $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length)}";
        });
    }

    private static string? HtmlToText(string? html) => string.IsNullOrWhiteSpace(html) ? null : StripTags(html);

    private static string StripTags(string html)
    {
        var withoutBlocks = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = Regex.Replace(withoutBlocks, @"<br\s*/?>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
    }

    private static List<WebmailAddress> Addresses(InternetAddressList list) =>
        list.Mailboxes.Select(m => new WebmailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address)).ToList();

    private static WebmailAddress? ParseFirst(string? header) =>
        header is not null && InternetAddressList.TryParse(header, out var list) ? Addresses(list).FirstOrDefault() : null;

    private static List<MailboxAddress> ParseList(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (!InternetAddressList.TryParse(value.Replace(';', ','), out var list)) throw new WebmailException($"{field}: check the email addresses.");
        var result = list.Mailboxes.ToList();
        var bad = result.FirstOrDefault(m => m.Address is null || m.Address.Count(c => c == '@') != 1 || m.Address.StartsWith('@') || m.Address.EndsWith('@') || !m.Address.Split('@')[1].Contains('.'));
        if (bad is not null) throw new WebmailException($"{field}: '{bad.Address ?? bad.Name}' is not a valid email address.");
        return result;
    }
}
