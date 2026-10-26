using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Mail;

/// <summary>Tenant-scoped view of the native outbound queue (one row per recipient).</summary>
public sealed class OutboundQueueService(IApplicationDbContext db, ITenantContext tenant, IOutboundMailer mailer, IAuditLogger audit, IClock clock)
{
    private static readonly DeliveryStatus[] Pending = [DeliveryStatus.Queued, DeliveryStatus.Sending, DeliveryStatus.Deferred];

    /// <param name="includeFinished">Also return delivered/failed/cancelled entries from the last 7 days.</param>
    public async Task<IReadOnlyList<QueuedMessage>> ListAsync(int limit, bool includeFinished, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-7);
        var query = db.OutboundRecipients.AsNoTracking()
            .Where(r => Pending.Contains(r.Status) || (includeFinished && r.UpdatedAt >= since));
        return await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(r => new QueuedMessage(
                r.Id.ToString(), r.OutboundMessage!.EnvelopeFrom, r.Address, r.OutboundMessage.SizeBytes, r.Status.ToString(),
                r.CreatedAt, r.LastResponse, r.Attempts, r.NextAttemptAt, r.RemoteHost, r.OutboundMessage.Subject, r.DeliveredAt))
            .ToListAsync(ct);
    }

    public async Task RetryNowAsync(Guid recipientId, CancellationToken ct)
    {
        var r = await db.OutboundRecipients.FirstOrDefaultAsync(x => x.Id == recipientId, ct) ?? throw new NotFoundException("Queue entry", recipientId);
        if (r.Status != DeliveryStatus.Deferred && r.Status != DeliveryStatus.Queued) throw new ConflictException("Only queued or deferred messages can be retried.");
        r.Status = DeliveryStatus.Queued;
        r.NextAttemptAt = clock.UtcNow;
        audit.Record("mail-queue.retried", nameof(OutboundRecipient), recipientId.ToString(), new { r.Address });
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(Guid recipientId, CancellationToken ct)
    {
        var r = await db.OutboundRecipients.FirstOrDefaultAsync(x => x.Id == recipientId, ct) ?? throw new NotFoundException("Queue entry", recipientId);
        if (r.Status is DeliveryStatus.Delivered or DeliveryStatus.Failed or DeliveryStatus.Cancelled) throw new ConflictException("This message is no longer queued.");
        if (r.Status == DeliveryStatus.Sending && r.LockedUntil > clock.UtcNow) throw new ConflictException("Delivery is in progress; try again in a moment.");
        r.Status = DeliveryStatus.Cancelled;
        r.LastResponse = "Cancelled by administrator";
        audit.Record("mail-queue.cancelled", nameof(OutboundRecipient), recipientId.ToString(), new { r.Address }, privileged: true);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a plain test message from a mailbox, to verify outbound delivery and DKIM before cutover.</summary>
    public async Task<Guid> SendTestMessageAsync(Guid mailboxId, string to, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailboxId && m.Status == MailboxStatus.Active, ct)
            ?? throw new NotFoundException(nameof(Mailbox), mailboxId);
        to = to.Trim();
        if (to.Length > 320 || to.Count(c => c == '@') != 1 || to.StartsWith('@') || to.EndsWith('@') || to.Any(char.IsWhiteSpace))
            throw new ValidationException(nameof(to), "Enter a valid email address.");

        var id = await mailer.EnqueueAsync(new OutboundEnvelope(
            mailbox.EmailAddress, mailbox.DisplayName, [to],
            "MX outbound test message",
            $"This is a test message sent from {mailbox.EmailAddress} by the MX mail server at {clock.UtcNow:u}.\r\n\r\nIf you received it, outbound delivery works.\r\n",
            OutboundSource.TestMessage, tenant.UserId), ct);
        audit.Record("mailbox.test-message-sent", nameof(Mailbox), mailboxId.ToString(), new { to }, privileged: false);
        await db.SaveChangesAsync(ct);
        return id;
    }
}
