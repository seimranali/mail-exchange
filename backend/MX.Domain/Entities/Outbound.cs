using MX.Domain.Common;

namespace MX.Domain.Entities;

/// <summary>A message waiting for (or finished with) outbound delivery. Content lives in the message store.</summary>
public class OutboundMessage : TenantEntity
{
    public OutboundSource Source { get; set; }
    /// <summary>SMTP envelope sender; empty for bounces (null reverse-path).</summary>
    public required string EnvelopeFrom { get; set; }
    public string? Subject { get; set; }
    public string? InternetMessageId { get; set; }
    public long SizeBytes { get; set; }
    public required string StoragePath { get; set; }
    public bool DkimSigned { get; set; }
    public Guid? CreatedByUserId { get; set; }
    /// <summary>Delivery is abandoned (and bounced) after this time.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public ICollection<OutboundRecipient> Recipients { get; set; } = [];
}

/// <summary>Delivery state for one recipient. Recipients on the same domain are sent in one SMTP transaction.</summary>
public class OutboundRecipient : TenantEntity
{
    public Guid OutboundMessageId { get; set; }
    public required string Address { get; set; }
    public required string Domain { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Queued;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Claim lease held by a delivery worker; expired leases are reclaimed after a crash.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastResponse { get; set; }
    public string? RemoteHost { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }

    public OutboundMessage? OutboundMessage { get; set; }
}

/// <summary>DKIM signing key for a domain. The private key is stored encrypted, never in plaintext.</summary>
public class DkimKey : TenantEntity
{
    public Guid DomainId { get; set; }
    public required string Selector { get; set; }
    /// <summary>Base64 SubjectPublicKeyInfo, published in DNS as p=.</summary>
    public required string PublicKey { get; set; }
    /// <summary>AES-256-GCM encrypted PKCS#8 private key (nonce | tag | ciphertext, base64).</summary>
    public required string EncryptedPrivateKey { get; set; }
    public int KeyBits { get; set; } = 2048;
    public bool IsActive { get; set; } = true;

    public OrganizationDomain? Domain { get; set; }
}
