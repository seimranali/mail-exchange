using MX.Domain;

namespace MX.Application.Abstractions;

/// <summary>Encrypts secrets (e.g. DKIM private keys) before they are stored. Never persist the plaintext.</summary>
public interface IKeyProtector
{
    string Protect(ReadOnlySpan<byte> plaintext, string purpose);
    byte[] Unprotect(string protectedValue, string purpose);
}

/// <summary>Access to quarantined message content held by the mail store.</summary>
public interface IQuarantineStore
{
    /// <summary>Delivers a released message to the recipient's Inbox. Returns false if the content no longer exists.</summary>
    Task<bool> DeliverReleasedAsync(Domain.Entities.QuarantineRecord record, CancellationToken ct);
    void DeleteContent(string storageRef);
}

/// <summary>A message to send, described without any MIME library types.</summary>
public sealed record OutboundEnvelope(
    string FromAddress,
    string? FromDisplayName,
    IReadOnlyList<string> To,
    string Subject,
    string TextBody,
    OutboundSource Source,
    Guid? CreatedByUserId);

/// <summary>Queues messages for outbound delivery (DKIM-signed when the sender's domain has a key).</summary>
public interface IOutboundMailer
{
    /// <summary>Runs in the caller's tenant scope. Returns the outbound message id.</summary>
    Task<Guid> EnqueueAsync(OutboundEnvelope envelope, CancellationToken ct);
}
