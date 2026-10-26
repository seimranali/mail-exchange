using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Authentication;
using MX.MailServer.Delivery;
using MX.MailServer.Storage;
using MX.Persistence;

namespace MX.MailServer.Filtering;

public interface IQuarantine
{
    Task HoldAsync(byte[] content, MimeMessage? message, string envelopeFrom, IReadOnlyCollection<LocalRecipient> recipients, FilterVerdict verdict, CancellationToken ct);
}

/// <summary>
/// Holds filtered messages for administrators (Security → Quarantine) and releases them on request.
/// One copy and one record per recipient, so release/delete is independent per mailbox and tenant.
/// </summary>
public sealed class QuarantineService(IServiceScopeFactory scopes, IMessageStore store, ILocalDelivery delivery, IOptions<FilterOptions> options, ILogger<QuarantineService> logger)
    : IQuarantine, IQuarantineStore
{
    public async Task HoldAsync(byte[] content, MimeMessage? message, string envelopeFrom, IReadOnlyCollection<LocalRecipient> recipients, FilterVerdict verdict, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var recipient in recipients.DistinctBy(r => r.MailboxId))
        {
            var id = Guid.CreateVersion7();
            var path = await store.SaveQuarantineAsync(recipient.OrganizationId, id, content, ct);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(recipient.OrganizationId, null);
                var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();
                db.QuarantineRecords.Add(new QuarantineRecord
                {
                    Id = id,
                    OrganizationId = recipient.OrganizationId,
                    MailboxId = recipient.MailboxId,
                    MessageId = Cap(message?.MessageId ?? $"<{id:N}@quarantine>", 998)!,
                    Sender = Cap(message?.From.Mailboxes.FirstOrDefault()?.Address ?? (envelopeFrom.Length > 0 ? envelopeFrom : "<>"), 320)!,
                    Recipient = recipient.Address,
                    Subject = Cap(message?.Subject, 998),
                    Reason = verdict.Reason ?? QuarantineReason.Spam,
                    SpamScore = Math.Min(verdict.Score, 9999),
                    SizeBytes = content.LongLength,
                    StorageRef = path,
                    ReceivedAt = now,
                    ExpiresAt = now.AddDays(options.Value.QuarantineDays),
                });
                if (verdict.Reason is QuarantineReason.Malware or QuarantineReason.Phishing)
                {
                    db.SecurityAlerts.Add(new SecurityAlert
                    {
                        OrganizationId = recipient.OrganizationId,
                        MailboxId = recipient.MailboxId,
                        Title = verdict.Reason == QuarantineReason.Malware ? "Malware blocked" : "Phishing message quarantined",
                        Category = verdict.Reason == QuarantineReason.Malware ? "Malware" : "Phishing",
                        Severity = verdict.Reason == QuarantineReason.Malware ? AlertSeverity.High : AlertSeverity.Medium,
                        Description = Cap($"Message to {recipient.Address} from {envelopeFrom} was quarantined. " +
                            string.Join("; ", verdict.Hits.Select(h => $"{h.Rule}: {h.Detail}")), 4000)!,
                        DetectedAt = now,
                    });
                }
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                store.Delete(path);
                throw;
            }
            logger.LogInformation("Quarantined message for {Recipient} ({Reason}, score {Score}): {Rules}", recipient.Address, verdict.Reason, verdict.Score, verdict.RulesSummary);
        }
    }

    public async Task<bool> DeliverReleasedAsync(QuarantineRecord record, CancellationToken ct)
    {
        if (record.MailboxId is not { } mailboxId || !store.Exists(record.StorageRef)) return false;

        byte[] content;
        await using (var file = store.OpenRead(record.StorageRef))
        using (var buffer = new MemoryStream())
        {
            await file.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        var none = new SpfOutcome(AuthCheckResult.None, "", "released from quarantine");
        var released = new ReceivedMessage(content, record.Sender, "", "quarantine", true, none,
            new DkimOutcome(AuthCheckResult.None, [], ""), new DmarcOutcome(AuthCheckResult.None, DmarcPolicy.None, "", ""));
        await delivery.DeliverAsync(released, [new LocalRecipient(record.OrganizationId, mailboxId, record.Recipient, record.Recipient, long.MaxValue, 0)], ct);
        store.Delete(record.StorageRef);
        return true;
    }

    public void DeleteContent(string storageRef)
    {
        if (store.Exists(storageRef)) store.Delete(storageRef);
    }

    private static string? Cap(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
