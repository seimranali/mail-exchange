using MX.Domain.Common;

namespace MX.Domain.Entities;

public class RetentionPolicy : TenantEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int RetentionDays { get; set; }
    public RetentionAction Action { get; set; }
    public bool AppliesToAllMailboxes { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
}

public class LegalHold : TenantEntity
{
    public required string Name { get; set; }
    public required string CaseReference { get; set; }
    public string? Description { get; set; }
    public LegalHoldStatus Status { get; set; } = LegalHoldStatus.Active;
    public Guid[] MailboxIds { get; set; } = [];
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public class DiscoveryRequest : TenantEntity
{
    public required string Name { get; set; }
    public Guid? LegalHoldId { get; set; }
    public required string Query { get; set; }
    public DateTimeOffset? DateFrom { get; set; }
    public DateTimeOffset? DateTo { get; set; }
    public Guid[] MailboxIds { get; set; } = [];
    public DiscoveryStatus Status { get; set; } = DiscoveryStatus.Draft;
    public int? ResultCount { get; set; }
    public string? ExportRef { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public LegalHold? LegalHold { get; set; }
}
