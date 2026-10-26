using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Outbound;
using MX.MailServer.Storage;
using MX.Persistence;

namespace MX.MailServer.Submission;

/// <summary>Result of a submission: an SMTP reply line.</summary>
public sealed record SubmissionResult(bool Accepted, string Reply);

public interface ISubmissionHandler
{
    Task<SubmissionResult> SubmitAsync(AuthenticatedMailbox user, string envelopeFrom, IReadOnlyList<string> recipients, byte[] content, CancellationToken ct);
}

/// <summary>
/// Accepts a message from an authenticated mail client: enforces sender identity and rate limits,
/// queues it for delivery (DKIM-signed), and files a copy in the sender's Sent folder.
/// </summary>
public sealed class SubmissionHandler(IServiceScopeFactory scopes, IMessageStore store, IOptions<MailServerOptions> options, ILogger<SubmissionHandler> logger) : ISubmissionHandler
{
    public async Task<SubmissionResult> SubmitAsync(AuthenticatedMailbox user, string envelopeFrom, IReadOnlyList<string> recipients, byte[] content, CancellationToken ct)
    {
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(new MemoryStream(content, writable: false), ct);
        }
        catch (FormatException)
        {
            return new SubmissionResult(false, "554 5.6.0 Message could not be parsed");
        }

        // The visible From must also be the user's own address; otherwise a logged-in user could
        // forge mail "from" a colleague while using their own envelope sender.
        var fromAddresses = message.From.Mailboxes.Select(m => m.Address).ToList();
        if (fromAddresses.Count == 0) return new SubmissionResult(false, "550 5.6.0 Message has no From header");
        var forbidden = fromAddresses.FirstOrDefault(a => !user.AllowedSenders.Contains(a));
        if (forbidden is not null) return new SubmissionResult(false, $"550 5.7.1 Not authorized to send as <{forbidden}>");

        // Clients must not get to choose these; the server sets them.
        message.Headers.RemoveAll(HeaderId.Bcc);
        message.Headers.RemoveAll(HeaderId.ReturnPath);

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(user.OrganizationId, null);
        var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();

        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var senders = user.AllowedSenders.ToArray();
        var sentLastHour = await db.OutboundMessages.CountAsync(m => m.Source == OutboundSource.Submission && senders.Contains(m.EnvelopeFrom) && m.CreatedAt >= since, ct);
        if (sentLastHour >= options.Value.MaxSubmissionsPerHour)
        {
            logger.LogWarning("Mailbox {Mailbox} hit the submission rate limit ({Limit}/hour)", user.Address, options.Value.MaxSubmissionsPerHour);
            return new SubmissionResult(false, "451 4.7.1 Sending rate limit reached; try again later");
        }

        var mailer = scope.ServiceProvider.GetRequiredService<OutboundMailer>();
        var outboundId = await mailer.EnqueueMessageAsync(message, envelopeFrom, recipients, OutboundSource.Submission, null, ct);
        await SaveSentCopyAsync(db, user, message, ct);
        return new SubmissionResult(true, $"250 2.0.0 OK queued as {outboundId:N}");
    }

    private async Task SaveSentCopyAsync(MxDbContext db, AuthenticatedMailbox user, MimeMessage message, CancellationToken ct)
    {
        byte[] content;
        using (var buffer = new MemoryStream())
        {
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            await message.WriteToAsync(format, buffer, ct);
            content = buffer.ToArray();
        }
        var id = Guid.CreateVersion7();
        var path = await store.SaveAsync(user.OrganizationId, user.MailboxId, id, content, ct);
        try
        {
            var uid = await UidAllocator.AllocateAsync(db, user.OrganizationId, user.MailboxId, MailFolder.Sent, 1, ct);
            db.MailMessages.Add(new MailMessage
            {
                Id = id,
                OrganizationId = user.OrganizationId,
                MailboxId = user.MailboxId,
                Folder = MailFolder.Sent,
                Uid = uid,
                InternetMessageId = message.MessageId,
                EnvelopeFrom = user.Address,
                FromHeader = message.From.ToString(),
                Subject = message.Subject is { Length: > 998 } s ? s[..998] : message.Subject,
                SizeBytes = content.LongLength,
                StoragePath = path,
                IsRead = true,
                ReceivedOverTls = true,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await db.Mailboxes.Where(m => m.Id == user.MailboxId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.UsedBytes, m => m.UsedBytes + content.LongLength), ct);
        }
        catch
        {
            store.Delete(path);
            throw;
        }
    }
}
