using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Utils;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Authentication;
using MX.MailServer.Storage;
using MX.Persistence;

namespace MX.MailServer.Delivery;

/// <summary>Everything known about a message at the end of DATA.</summary>
public sealed record ReceivedMessage(
    byte[] Content,
    string EnvelopeFrom,
    string RemoteIp,
    string HeloName,
    bool OverTls,
    SpfOutcome Spf,
    DkimOutcome Dkim,
    DmarcOutcome Dmarc,
    decimal? SpamScore = null,
    bool Junk = false);

public interface ILocalDelivery
{
    Task DeliverAsync(ReceivedMessage message, IReadOnlyCollection<LocalRecipient> recipients, CancellationToken ct);
}

/// <summary>
/// Writes one copy per destination mailbox to the message store and indexes it in the
/// management database under that mailbox's tenant scope.
/// </summary>
public sealed class LocalDelivery(IServiceScopeFactory scopes, IMessageStore store, ILogger<LocalDelivery> logger) : ILocalDelivery
{
    public async Task DeliverAsync(ReceivedMessage message, IReadOnlyCollection<LocalRecipient> recipients, CancellationToken ct)
    {
        var headers = ParseHeaders(message.Content);
        var folder = ChooseFolder(message);

        // An alias and its mailbox in the same transaction must not produce two copies.
        foreach (var recipient in recipients.DistinctBy(r => r.MailboxId))
        {
            var id = Guid.CreateVersion7();
            var path = await store.SaveAsync(recipient.OrganizationId, recipient.MailboxId, id, message.Content, ct);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(recipient.OrganizationId, null);
                var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();

                var uid = await UidAllocator.AllocateAsync(db, recipient.OrganizationId, recipient.MailboxId, folder, 1, ct);
                db.MailMessages.Add(new MailMessage
                {
                    Id = id,
                    OrganizationId = recipient.OrganizationId,
                    MailboxId = recipient.MailboxId,
                    Folder = folder,
                    Uid = uid,
                    InternetMessageId = Truncate(headers.MessageId, 998),
                    EnvelopeFrom = Truncate(message.EnvelopeFrom, 320)!,
                    FromHeader = Truncate(headers.From, 998),
                    Subject = Truncate(headers.Subject, 998),
                    SizeBytes = message.Content.LongLength,
                    StoragePath = path,
                    RemoteIp = message.RemoteIp,
                    HeloName = Truncate(message.HeloName, 255),
                    ReceivedOverTls = message.OverTls,
                    SpfResult = message.Spf.Result,
                    DkimResult = message.Dkim.Result,
                    DmarcResult = message.Dmarc.Result,
                    SpamScore = message.SpamScore,
                    ReceivedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                // Atomic increment; avoids lost updates when messages arrive concurrently.
                await db.Mailboxes.Where(m => m.Id == recipient.MailboxId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.UsedBytes, m => m.UsedBytes + message.Content.LongLength), ct);
            }
            catch
            {
                store.Delete(path); // never leave an unindexed file behind
                throw;
            }
            logger.LogInformation("Delivered message {MessageId} to mailbox {MailboxId} ({Folder}, {Size} bytes)", id, recipient.MailboxId, folder, message.Content.Length);
        }
    }

    internal static MailFolder ChooseFolder(ReceivedMessage m)
    {
        if (m.Junk) return MailFolder.Junk;
        if (m.Dmarc.Result == AuthCheckResult.Fail && m.Dmarc.Policy != DmarcPolicy.None) return MailFolder.Junk;
        if (m.Dmarc.Result == AuthCheckResult.None && m.Spf.Result == AuthCheckResult.Fail) return MailFolder.Junk;
        return MailFolder.Inbox;
    }

    private static (string? From, string? Subject, string? MessageId) ParseHeaders(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var headers = HeaderList.Load(stream);
            return (Decode(headers[HeaderId.From]), Decode(headers[HeaderId.Subject]), headers[HeaderId.MessageId]);
        }
        catch (FormatException)
        {
            return (null, null, null);
        }
    }

    // Decodes RFC 2047 encoded-words such as =?UTF-8?B?...?= in Subject and From.
    private static string? Decode(string? value) => value is null ? null : Rfc2047.DecodeText(System.Text.Encoding.UTF8.GetBytes(value));

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value.Trim() : value[..max];
}
