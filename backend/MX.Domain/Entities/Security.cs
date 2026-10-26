using System.Text.Json;
using MX.Domain.Common;

namespace MX.Domain.Entities;

public class SecurityPolicy : TenantEntity
{
    public int PasswordMinLength { get; set; } = 12;
    public bool RequireMfa { get; set; }
    public int SessionTimeoutMinutes { get; set; } = 60;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public bool EnforceTls { get; set; } = true;
    public string[] AllowedIpRanges { get; set; } = [];
}

/// <summary>Append-only record of sign-in attempts.</summary>
public class AuthenticationEvent : Entity
{
    /// <summary>Null when the attempt could not be associated with a known user.</summary>
    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public required string AttemptedEmail { get; set; }
    public AuthenticationOutcome Outcome { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Append-only audit trail.</summary>
public class AuditEvent : Entity
{
    /// <summary>Null for platform-level events.</summary>
    public Guid? OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public bool IsPrivileged { get; set; }
    public JsonDocument? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public class SecurityAlert : TenantEntity
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Open;
    public Guid? MailboxId { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>Metadata for a quarantined message. Message content lives in the mail store.</summary>
public class QuarantineRecord : TenantEntity
{
    public Guid? MailboxId { get; set; }
    public required string MessageId { get; set; }
    public required string Sender { get; set; }
    public required string Recipient { get; set; }
    public string? Subject { get; set; }
    public QuarantineReason Reason { get; set; }
    public QuarantineStatus Status { get; set; } = QuarantineStatus.Held;
    public decimal? SpamScore { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Opaque reference into the mail storage system.</summary>
    public required string StorageRef { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ReleasedByUserId { get; set; }
}
