using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Storage;
using MX.MailServer.Submission;
using MX.Persistence;

namespace MX.MailServer.Imap;

/// <summary>Per-message state an IMAP session keeps for its selected folder.</summary>
public sealed record ImapMessage(Guid Id, long Uid, long Size, DateTimeOffset InternalDate, string StoragePath, ImapFlags Flags);

[Flags]
public enum ImapFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Deleted = 8,
    Draft = 16,
}

public sealed record FolderStatus(int Messages, int Unseen, long UidNext, long UidValidity, long? FirstUnseenSeq);

/// <summary>
/// Mail access for one authenticated mailbox. <b>Every query filters by MailboxId</b>: RLS only
/// separates organizations, and colleagues in the same organization must not see each other's mail.
/// </summary>
public sealed class ImapMailStore(IServiceScopeFactory scopes, IMessageStore store, AuthenticatedMailbox user)
{
    public static readonly IReadOnlyDictionary<MailFolder, (string Name, string SpecialUse)> Folders = new Dictionary<MailFolder, (string, string)>
    {
        [MailFolder.Inbox] = ("INBOX", ""),
        [MailFolder.Sent] = ("Sent", @"\Sent"),
        [MailFolder.Drafts] = ("Drafts", @"\Drafts"),
        [MailFolder.Junk] = ("Junk", @"\Junk"),
        [MailFolder.Trash] = ("Trash", @"\Trash"),
        [MailFolder.Archive] = ("Archive", @"\Archive"),
    };

    public AuthenticatedMailbox User => user;

    public static MailFolder? ResolveFolder(string name)
    {
        name = name.Trim('/');
        foreach (var (folder, info) in Folders)
            if (info.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return folder;
        return null;
    }

    private async Task<T> WithDbAsync<T>(Func<MxDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(user.OrganizationId, null);
        return await work(scope.ServiceProvider.GetRequiredService<MxDbContext>());
    }

    private IQueryable<MailMessage> Messages(MxDbContext db, MailFolder folder) =>
        db.MailMessages.Where(m => m.MailboxId == user.MailboxId && m.Folder == folder);

    public Task<List<ImapMessage>> ListAsync(MailFolder folder, CancellationToken ct) => WithDbAsync(db =>
        Messages(db, folder).AsNoTracking().OrderBy(m => m.Uid)
            .Select(m => new ImapMessage(m.Id, m.Uid, m.SizeBytes, m.ReceivedAt, m.StoragePath,
                (m.IsRead ? ImapFlags.Seen : 0) | (m.IsAnswered ? ImapFlags.Answered : 0) | (m.IsFlagged ? ImapFlags.Flagged : 0)
                | (m.IsDeleted ? ImapFlags.Deleted : 0) | (m.IsDraft ? ImapFlags.Draft : 0)))
            .ToListAsync(ct));

    public Task<FolderStatus> StatusAsync(MailFolder folder, CancellationToken ct) => WithDbAsync(async db =>
    {
        var (validity, next) = await UidAllocator.GetStateAsync(db, user.OrganizationId, user.MailboxId, folder, ct);
        var uids = await Messages(db, folder).OrderBy(m => m.Uid).Select(m => new { m.Uid, m.IsRead }).ToListAsync(ct);
        var firstUnseen = uids.FindIndex(u => !u.IsRead);
        return new FolderStatus(uids.Count, uids.Count(u => !u.IsRead), next, validity, firstUnseen >= 0 ? firstUnseen + 1 : null);
    });

    /// <summary>Writes the final flag set of each message (one UPDATE per distinct flag combination).</summary>
    public Task<int> SetFlagsAsync(IEnumerable<(Guid Id, ImapFlags Flags)> changes, CancellationToken ct) => WithDbAsync(async db =>
    {
        var total = 0;
        foreach (var group in changes.GroupBy(c => c.Flags))
        {
            var flags = group.Key;
            var ids = group.Select(c => c.Id).ToList();
            total += await db.MailMessages.Where(m => m.MailboxId == user.MailboxId && ids.Contains(m.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsRead, (flags & ImapFlags.Seen) != 0)
                .SetProperty(m => m.IsAnswered, (flags & ImapFlags.Answered) != 0)
                .SetProperty(m => m.IsFlagged, (flags & ImapFlags.Flagged) != 0)
                .SetProperty(m => m.IsDeleted, (flags & ImapFlags.Deleted) != 0)
                .SetProperty(m => m.IsDraft, (flags & ImapFlags.Draft) != 0)
                .SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
        return total;
    });

    /// <summary>Copies messages into another folder (new files and UIDs). Returns (source UID, new UID) pairs.</summary>
    public Task<(long UidValidity, List<(long From, long To)> Map)> CopyAsync(MailFolder target, IReadOnlyList<ImapMessage> messages, CancellationToken ct) => WithDbAsync(async db =>
    {
        var map = new List<(long, long)>();
        if (messages.Count == 0) return ((await UidAllocator.GetStateAsync(db, user.OrganizationId, user.MailboxId, target, ct)).UidValidity, map);
        await EnsureQuotaAsync(db, messages.Sum(m => m.Size), ct);

        var ids = messages.Select(m => m.Id).ToList();
        var sources = await db.MailMessages.AsNoTracking().Where(m => m.MailboxId == user.MailboxId && ids.Contains(m.Id)).OrderBy(m => m.Uid).ToListAsync(ct);
        var firstUid = await UidAllocator.AllocateAsync(db, user.OrganizationId, user.MailboxId, target, sources.Count, ct);
        var written = new List<string>();
        try
        {
            long total = 0;
            for (var i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                byte[] content;
                await using (var file = store.OpenRead(src.StoragePath))
                using (var buffer = new MemoryStream())
                {
                    await file.CopyToAsync(buffer, ct);
                    content = buffer.ToArray();
                }
                var id = Guid.CreateVersion7();
                var path = await store.SaveAsync(user.OrganizationId, user.MailboxId, id, content, ct);
                written.Add(path);
                db.MailMessages.Add(new MailMessage
                {
                    Id = id, OrganizationId = user.OrganizationId, MailboxId = user.MailboxId, Folder = target, Uid = firstUid + i,
                    InternetMessageId = src.InternetMessageId, EnvelopeFrom = src.EnvelopeFrom, FromHeader = src.FromHeader, Subject = src.Subject,
                    SizeBytes = src.SizeBytes, StoragePath = path, IsRead = src.IsRead, IsFlagged = src.IsFlagged, IsAnswered = src.IsAnswered,
                    IsDraft = src.IsDraft, IsDeleted = false, RemoteIp = src.RemoteIp, HeloName = src.HeloName, ReceivedOverTls = src.ReceivedOverTls,
                    SpfResult = src.SpfResult, DkimResult = src.DkimResult, DmarcResult = src.DmarcResult, ReceivedAt = src.ReceivedAt,
                });
                map.Add((src.Uid, firstUid + i));
                total += src.SizeBytes;
            }
            await db.SaveChangesAsync(ct);
            await AdjustUsageAsync(db, total, ct);
        }
        catch
        {
            foreach (var p in written) store.Delete(p);
            throw;
        }
        var state = await UidAllocator.GetStateAsync(db, user.OrganizationId, user.MailboxId, target, ct);
        return (state.UidValidity, map);
    });

    /// <summary>Moves messages (same files, new UIDs in the target folder).</summary>
    public Task<(long UidValidity, List<(long From, long To)> Map)> MoveAsync(MailFolder target, IReadOnlyList<ImapMessage> messages, CancellationToken ct) => WithDbAsync(async db =>
    {
        var map = new List<(long, long)>();
        var ordered = messages.OrderBy(m => m.Uid).ToList();
        if (ordered.Count > 0)
        {
            var firstUid = await UidAllocator.AllocateAsync(db, user.OrganizationId, user.MailboxId, target, ordered.Count, ct);
            for (var i = 0; i < ordered.Count; i++)
            {
                var id = ordered[i].Id;
                var newUid = firstUid + i;
                await db.MailMessages.Where(m => m.MailboxId == user.MailboxId && m.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Folder, target).SetProperty(m => m.Uid, newUid).SetProperty(m => m.IsDeleted, false), ct);
                map.Add((ordered[i].Uid, newUid));
            }
        }
        var state = await UidAllocator.GetStateAsync(db, user.OrganizationId, user.MailboxId, target, ct);
        return (state.UidValidity, map);
    });

    /// <summary>Permanently removes messages and their files.</summary>
    public Task<int> ExpungeAsync(IReadOnlyList<ImapMessage> messages, CancellationToken ct) => WithDbAsync(async db =>
    {
        if (messages.Count == 0) return 0;
        var ids = messages.Select(m => m.Id).ToList();
        // Mailboxes under an active legal hold keep everything.
        var onHold = await db.LegalHolds.AnyAsync(h => h.Status == LegalHoldStatus.Active && h.MailboxIds.Contains(user.MailboxId), ct);
        if (onHold) throw new ImapNoException("[CANNOT] Mailbox is under legal hold; messages cannot be permanently deleted");

        var rows = await db.MailMessages.Where(m => m.MailboxId == user.MailboxId && ids.Contains(m.Id)).Select(m => new { m.StoragePath, m.SizeBytes }).ToListAsync(ct);
        var deleted = await db.MailMessages.Where(m => m.MailboxId == user.MailboxId && ids.Contains(m.Id)).ExecuteDeleteAsync(ct);
        await AdjustUsageAsync(db, -rows.Sum(r => r.SizeBytes), ct);
        foreach (var r in rows) store.Delete(r.StoragePath);
        return deleted;
    });

    public Task<(long UidValidity, long Uid)> AppendAsync(MailFolder folder, byte[] content, ImapFlags flags, DateTimeOffset? internalDate, CancellationToken ct) => WithDbAsync(async db =>
    {
        await EnsureQuotaAsync(db, content.LongLength, ct);
        var headers = ParseHeaders(content);
        var id = Guid.CreateVersion7();
        var path = await store.SaveAsync(user.OrganizationId, user.MailboxId, id, content, ct);
        try
        {
            var uid = await UidAllocator.AllocateAsync(db, user.OrganizationId, user.MailboxId, folder, 1, ct);
            db.MailMessages.Add(new MailMessage
            {
                Id = id, OrganizationId = user.OrganizationId, MailboxId = user.MailboxId, Folder = folder, Uid = uid,
                InternetMessageId = headers.MessageId, EnvelopeFrom = user.Address, FromHeader = headers.From, Subject = headers.Subject,
                SizeBytes = content.LongLength, StoragePath = path,
                IsRead = (flags & ImapFlags.Seen) != 0, IsAnswered = (flags & ImapFlags.Answered) != 0, IsFlagged = (flags & ImapFlags.Flagged) != 0,
                IsDraft = (flags & ImapFlags.Draft) != 0, IsDeleted = (flags & ImapFlags.Deleted) != 0,
                ReceivedOverTls = true, ReceivedAt = internalDate ?? DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await AdjustUsageAsync(db, content.LongLength, ct);
            var state = await UidAllocator.GetStateAsync(db, user.OrganizationId, user.MailboxId, folder, ct);
            return (state.UidValidity, uid);
        }
        catch
        {
            store.Delete(path);
            throw;
        }
    });

    public async Task<byte[]> ReadContentAsync(ImapMessage message, CancellationToken ct)
    {
        await using var file = store.OpenRead(message.StoragePath);
        using var buffer = new MemoryStream((int)Math.Min(message.Size, int.MaxValue));
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private async Task EnsureQuotaAsync(MxDbContext db, long additional, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.AsNoTracking().Where(m => m.Id == user.MailboxId).Select(m => new { m.UsedBytes, m.QuotaBytes }).SingleAsync(ct);
        if (mailbox.UsedBytes + additional > mailbox.QuotaBytes) throw new ImapNoException("[OVERQUOTA] Mailbox quota exceeded");
    }

    private Task AdjustUsageAsync(MxDbContext db, long delta, CancellationToken ct) =>
        db.Mailboxes.Where(m => m.Id == user.MailboxId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.UsedBytes, m => m.UsedBytes + delta < 0 ? 0 : m.UsedBytes + delta), ct);

    private static (string? From, string? Subject, string? MessageId) ParseHeaders(byte[] content)
    {
        try
        {
            var headers = MimeKit.HeaderList.Load(new MemoryStream(content, writable: false));
            static string? Cap(string? v) => v is { Length: > 998 } ? v[..998] : v;
            return (Cap(headers[MimeKit.HeaderId.From]), Cap(headers[MimeKit.HeaderId.Subject]), Cap(headers[MimeKit.HeaderId.MessageId]));
        }
        catch (FormatException)
        {
            return (null, null, null);
        }
    }
}

/// <summary>Raised to produce a tagged NO response with the given text.</summary>
public sealed class ImapNoException(string message) : Exception(message);
