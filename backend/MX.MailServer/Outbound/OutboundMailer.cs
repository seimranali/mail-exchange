using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Application.Mail;
using MX.Domain;
using MX.Domain.Entities;
using MX.MailServer.Storage;
using Org.BouncyCastle.Security;

namespace MX.MailServer.Outbound;

/// <summary>
/// Queues messages for delivery. Runs in the caller's tenant scope: the sender's domain and DKIM
/// key are looked up through tenant-filtered queries, so one tenant can never sign as another.
/// </summary>
public sealed class OutboundMailer(
    IApplicationDbContext db,
    ITenantContext tenant,
    IKeyProtector protector,
    IMessageStore store,
    IOptions<OutboundOptions> options,
    IOptions<MailServerOptions> serverOptions,
    IClock clock,
    ILogger<OutboundMailer> logger) : IOutboundMailer
{
    private static readonly FormatOptions WireFormat = CreateWireFormat();

    public Task<Guid> EnqueueAsync(OutboundEnvelope envelope, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(envelope.FromDisplayName, envelope.FromAddress));
        foreach (var to in envelope.To) message.To.Add(MailboxAddress.Parse(to));
        message.Subject = envelope.Subject;
        message.Body = new TextPart("plain") { Text = envelope.TextBody };
        // MimeKit pre-fills Message-ID with the machine's host name; use the sender's domain instead.
        message.MessageId = $"{Guid.CreateVersion7():N}@{envelope.FromAddress[(envelope.FromAddress.LastIndexOf('@') + 1)..]}";
        return EnqueueMessageAsync(message, envelope.FromAddress, envelope.To, envelope.Source, envelope.CreatedByUserId, ct);
    }

    /// <summary>Queues an already-built message (submission, bounces).</summary>
    public async Task<Guid> EnqueueMessageAsync(MimeMessage message, string envelopeFrom, IReadOnlyCollection<string> recipients,
        OutboundSource source, Guid? createdByUserId, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId ?? throw new ForbiddenException("Select an organization first: platform administrators must choose which organization to manage.");
        if (recipients.Count == 0) throw new ValidationException("to", "At least one recipient is required.");

        // Bounces use the null reverse-path; everything else must come from one of this tenant's domains.
        var fromDomain = message.From.Mailboxes.FirstOrDefault()?.Domain?.ToLowerInvariant();
        OrganizationDomain? domain = null;
        if (source != OutboundSource.Bounce)
        {
            domain = await db.OrganizationDomains.AsNoTracking().FirstOrDefaultAsync(d => d.DomainName == fromDomain, ct)
                ?? throw new ForbiddenException($"'{fromDomain}' is not a domain of this organization.");
        }

        message.Date = message.Date == default ? clock.UtcNow : message.Date;
        if (string.IsNullOrEmpty(message.MessageId))
            message.MessageId = $"{Guid.CreateVersion7():N}@{fromDomain ?? serverOptions.Value.Hostname}";

        var signed = domain is not null && await TrySignAsync(message, domain, ct);

        byte[] content;
        using (var buffer = new MemoryStream())
        {
            await message.WriteToAsync(WireFormat, buffer, ct);
            content = buffer.ToArray();
        }

        var id = Guid.CreateVersion7();
        var path = await store.SaveOutboundAsync(orgId, id, content, ct);
        var now = clock.UtcNow;
        try
        {
            var outbound = new OutboundMessage
            {
                Id = id,
                OrganizationId = orgId,
                Source = source,
                EnvelopeFrom = envelopeFrom,
                Subject = Truncate(message.Subject, 998),
                InternetMessageId = Truncate(message.MessageId, 998),
                SizeBytes = content.LongLength,
                StoragePath = path,
                DkimSigned = signed,
                CreatedByUserId = createdByUserId,
                ExpiresAt = now.AddHours(options.Value.MaxQueueLifetimeHours),
            };
            foreach (var address in recipients.Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                outbound.Recipients.Add(new OutboundRecipient
                {
                    OrganizationId = orgId,
                    Address = address,
                    Domain = address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant(),
                    NextAttemptAt = now,
                });
            }
            db.OutboundMessages.Add(outbound);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            store.Delete(path);
            throw;
        }
        logger.LogInformation("Queued outbound message {MessageId} ({Source}, {Recipients} recipients, DKIM {Signed})", id, source, recipients.Count, signed);
        return id;
    }

    private async Task<bool> TrySignAsync(MimeMessage message, OrganizationDomain domain, CancellationToken ct)
    {
        var key = await db.DkimKeys.AsNoTracking().FirstOrDefaultAsync(k => k.DomainId == domain.Id && k.IsActive, ct);
        if (key is null) return false;

        var pkcs8 = protector.Unprotect(key.EncryptedPrivateKey, DkimKeyService.Purpose(domain.Id));
        try
        {
            var signer = new DkimSigner(PrivateKeyFactory.CreateKey(pkcs8), domain.DomainName, key.Selector, DkimSignatureAlgorithm.RsaSha256)
            {
                HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            };
            message.Prepare(EncodingConstraint.SevenBit);
            signer.Sign(WireFormat, message, [HeaderId.From, HeaderId.To, HeaderId.Cc, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId, HeaderId.MimeVersion, HeaderId.ContentType]);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static FormatOptions CreateWireFormat()
    {
        var o = FormatOptions.Default.Clone();
        o.NewLineFormat = NewLineFormat.Dos; // SMTP requires CRLF; signature and bytes on the wire must match
        return o;
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
