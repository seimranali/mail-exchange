using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Mail;

/// <summary>Envelope and authentication facts only; message content is never exposed to administrators here.</summary>
public sealed record MessageSummaryDto(Guid Id, MailFolder Folder, string EnvelopeFrom, string? FromHeader, string? Subject, long SizeBytes,
    string? RemoteIp, bool ReceivedOverTls, AuthCheckResult SpfResult, AuthCheckResult DkimResult, AuthCheckResult DmarcResult, DateTimeOffset ReceivedAt, decimal? SpamScore);

public sealed class MessageService(IApplicationDbContext db, IAuditLogger audit)
{
    public async Task<PagedResult<MessageSummaryDto>> ListAsync(Guid mailboxId, MailFolder? folder, PageRequest page, CancellationToken ct)
    {
        // Tenant filter: a mailbox from another organization is simply not found.
        if (!await db.Mailboxes.AnyAsync(m => m.Id == mailboxId, ct)) throw new NotFoundException(nameof(Mailbox), mailboxId);

        var query = db.MailMessages.AsNoTracking().Where(m => m.MailboxId == mailboxId);
        if (folder is { } f) query = query.Where(m => m.Folder == f);
        var result = await query.OrderByDescending(m => m.ReceivedAt)
            .Select(m => new MessageSummaryDto(m.Id, m.Folder, m.EnvelopeFrom, m.FromHeader, m.Subject, m.SizeBytes, m.RemoteIp,
                m.ReceivedOverTls, m.SpfResult, m.DkimResult, m.DmarcResult, m.ReceivedAt, m.SpamScore))
            .ToPagedAsync(page, ct);

        audit.Record("mailbox.messages-listed", nameof(Mailbox), mailboxId.ToString(), new { folder, page.SafePage }, privileged: true);
        await db.SaveChangesAsync(ct);
        return result;
    }
}
