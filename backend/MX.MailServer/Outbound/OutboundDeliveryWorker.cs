using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Authentication;
using MX.MailServer.Delivery;
using MX.MailServer.Storage;
using MX.Persistence;

namespace MX.MailServer.Outbound;

/// <summary>
/// Claims due recipients from the queue and delivers them, grouped by message and domain.
/// Claims use FOR UPDATE SKIP LOCKED with a lease, so several mail hosts can run safely and a
/// crashed host's work is picked up again when its lease expires.
/// </summary>
public sealed class OutboundDeliveryWorker(
    IServiceScopeFactory scopes,
    SmtpDeliveryClient client,
    IRecipientDirectory directory,
    ILocalDelivery localDelivery,
    IMessageStore store,
    IOptions<OutboundOptions> options,
    IOptions<MailServerOptions> serverOptions,
    ILogger<OutboundDeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbound delivery pass failed");
                processed = 0;
            }
            // Keep draining while there is work; otherwise poll.
            if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>One claim-and-deliver pass. Returns the number of recipients processed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        if (claimed.Count == 0) return 0;

        var groups = claimed.GroupBy(r => (r.OutboundMessageId, r.Domain)).ToList();
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxConcurrentDeliveries, CancellationToken = ct },
            async (group, token) =>
            {
                try
                {
                    await DeliverGroupAsync(group.First().OutboundMessage!, group.Key.Domain, group.ToList(), token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Delivery of message {MessageId} to {Domain} failed unexpectedly", group.Key.OutboundMessageId, group.Key.Domain);
                    await RecordAsync(group.ToList(), group.ToDictionary(r => r.Id, r => new RecipientResult(r.Address, RecipientOutcome.Deferred, "Internal error; will retry")), null, token);
                }
            });
        return claimed.Count;
    }

    private async Task<List<OutboundRecipient>> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope(null);
        var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();
        var leaseUntil = DateTimeOffset.UtcNow.Add(Lease);

        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE mx.outbound_recipients r
               SET status = 'Sending', locked_until = {leaseUntil}, attempts = r.attempts + 1, updated_at = now()
             WHERE r.id IN (
                   SELECT id FROM mx.outbound_recipients
                    WHERE (status IN ('Queued', 'Deferred') AND next_attempt_at <= now())
                       OR (status = 'Sending' AND locked_until < now())
                    ORDER BY next_attempt_at
                    LIMIT {options.Value.BatchSize}
                    FOR UPDATE SKIP LOCKED)
            RETURNING r.id AS "Value"
            """).ToListAsync(ct);
        if (ids.Count == 0) return [];
        return await db.OutboundRecipients.AsNoTracking().Include(r => r.OutboundMessage).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
    }

    private async Task DeliverGroupAsync(OutboundMessage message, string domain, List<OutboundRecipient> recipients, CancellationToken ct)
    {
        byte[] content;
        try
        {
            await using var file = store.OpenRead(message.StoragePath);
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            await RecordAsync(recipients, recipients.ToDictionary(r => r.Id, r => new RecipientResult(r.Address, RecipientOutcome.Failed, "Message content missing from store")), null, ct);
            return;
        }

        Dictionary<Guid, RecipientResult> results;
        string? remoteHost;
        if (await IsLocalDomainAsync(domain, ct))
        {
            (results, remoteHost) = (await DeliverLocallyAsync(message, recipients, content, ct), serverOptions.Value.Hostname);
        }
        else
        {
            var attempt = await client.DeliverAsync(domain, message.EnvelopeFrom, recipients.Select(r => r.Address).ToList(), content, ct);
            var byAddress = attempt.Results.ToDictionary(r => r.Address, StringComparer.OrdinalIgnoreCase);
            results = recipients.ToDictionary(r => r.Id, r => byAddress.GetValueOrDefault(r.Address) ?? new RecipientResult(r.Address, RecipientOutcome.Deferred, "No result"));
            remoteHost = attempt.RemoteHost;
            logger.LogInformation("Message {MessageId} to {Domain} via {Host}: {Transport}", message.Id, domain, attempt.RemoteHost, attempt.UsedTls ? "TLS (STARTTLS)" : "PLAINTEXT");
        }
        await RecordAsync(recipients, results, remoteHost, ct);
    }

    private async Task<bool> IsLocalDomainAsync(string domain, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope(null);
        return await scope.ServiceProvider.GetRequiredService<MxDbContext>().OrganizationDomains.AnyAsync(d => d.DomainName == domain, ct);
    }

    /// <summary>Mail between domains hosted here never leaves the server.</summary>
    private async Task<Dictionary<Guid, RecipientResult>> DeliverLocallyAsync(OutboundMessage message, List<OutboundRecipient> recipients, byte[] content, CancellationToken ct)
    {
        var results = new Dictionary<Guid, RecipientResult>();
        var deliverable = new List<(OutboundRecipient Row, LocalRecipient Target)>();
        foreach (var r in recipients)
        {
            var (status, target) = await directory.ResolveAsync(r.Address, ct);
            switch (status)
            {
                case RecipientStatus.Accepted: deliverable.Add((r, target!)); break;
                case RecipientStatus.OverQuota: results[r.Id] = new RecipientResult(r.Address, RecipientOutcome.Deferred, "452 4.2.2 Mailbox full"); break;
                case RecipientStatus.Disabled: results[r.Id] = new RecipientResult(r.Address, RecipientOutcome.Failed, "550 5.2.1 Mailbox disabled"); break;
                default: results[r.Id] = new RecipientResult(r.Address, RecipientOutcome.Failed, "550 5.1.1 No such user here"); break;
            }
        }
        if (deliverable.Count > 0)
        {
            var internalAuth = new SpfOutcome(AuthCheckResult.None, "", "internal delivery");
            var received = new ReceivedMessage(content, message.EnvelopeFrom, "127.0.0.1", serverOptions.Value.Hostname, true,
                internalAuth, new DkimOutcome(AuthCheckResult.None, [], "internal"), new DmarcOutcome(AuthCheckResult.None, DmarcPolicy.None, "", "internal"));
            await localDelivery.DeliverAsync(received, deliverable.Select(d => d.Target).ToList(), ct);
            foreach (var (row, _) in deliverable) results[row.Id] = new RecipientResult(row.Address, RecipientOutcome.Delivered, "250 2.0.0 Delivered locally");
        }
        return results;
    }

    private async Task RecordAsync(List<OutboundRecipient> recipients, Dictionary<Guid, RecipientResult> results, string? remoteHost, CancellationToken ct)
    {
        var orgId = recipients[0].OrganizationId;
        var messageId = recipients[0].OutboundMessageId;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(orgId, null);
        var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();

        var now = DateTimeOffset.UtcNow;
        var ids = recipients.Select(r => r.Id).ToList();
        var rows = await db.OutboundRecipients.Include(r => r.OutboundMessage).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var failures = new List<(string Address, string Response)>();
        foreach (var row in rows)
        {
            if (row.Status == DeliveryStatus.Cancelled) continue; // cancelled while we were sending
            var result = results[row.Id];
            row.LastResponse = Truncate(result.Response, 1000);
            row.RemoteHost = remoteHost;
            row.LockedUntil = null;
            switch (result.Outcome)
            {
                case RecipientOutcome.Delivered:
                    row.Status = DeliveryStatus.Delivered;
                    row.DeliveredAt = now;
                    break;
                case RecipientOutcome.Deferred when now < row.OutboundMessage!.ExpiresAt:
                    row.Status = DeliveryStatus.Deferred;
                    row.NextAttemptAt = now + RetrySchedule.After(row.Attempts);
                    break;
                default:
                    row.Status = DeliveryStatus.Failed;
                    if (result.Outcome == RecipientOutcome.Deferred) row.LastResponse = Truncate($"Gave up after {row.Attempts} attempts: {result.Response}", 1000);
                    failures.Add((row.Address, row.LastResponse!));
                    break;
            }
            logger.LogInformation("Recipient {Recipient} of {MessageId}: {Status} ({Response})", row.Address, messageId, row.Status, row.LastResponse);
        }
        await db.SaveChangesAsync(ct);

        var message = rows[0].OutboundMessage!;
        if (failures.Count > 0 && message.Source != OutboundSource.Bounce && message.EnvelopeFrom.Length > 0)
            await SendBounceAsync(scope.ServiceProvider, message, failures, ct);

        // Remove the queued file once no recipient needs it any more.
        var stillPending = await db.OutboundRecipients.AnyAsync(r => r.OutboundMessageId == messageId &&
            (r.Status == DeliveryStatus.Queued || r.Status == DeliveryStatus.Deferred || r.Status == DeliveryStatus.Sending), ct);
        if (!stillPending) store.Delete(message.StoragePath);
    }

    private async Task SendBounceAsync(IServiceProvider services, OutboundMessage original, List<(string Address, string Response)> failures, CancellationToken ct)
    {
        try
        {
            var bounce = BounceBuilder.Build(serverOptions.Value.Hostname, original.EnvelopeFrom, original.Subject, original.InternetMessageId, failures, original.CreatedAt);
            var mailer = services.GetRequiredService<OutboundMailer>();
            // Null reverse-path: a bounce must never generate another bounce (RFC 5321 section 4.5.5).
            await mailer.EnqueueMessageAsync(bounce, "", [original.EnvelopeFrom], OutboundSource.Bounce, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue bounce for message {MessageId}", original.Id);
        }
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
