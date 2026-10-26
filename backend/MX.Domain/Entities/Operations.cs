using System.Text.Json;
using MX.Domain.Common;

namespace MX.Domain.Entities;

public class BackupJob : TenantEntity
{
    public BackupScope Scope { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? StorageLocation { get; set; }
    public long? SizeBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class RestoreJob : TenantEntity
{
    public Guid BackupJobId { get; set; }
    public Guid? TargetMailboxId { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? ErrorMessage { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BackupJob? BackupJob { get; set; }
}

/// <summary>Point-in-time health sample for a mail server. Shared infrastructure.</summary>
public class ServerHealthSnapshot : Entity
{
    public Guid MailServerId { get; set; }
    public decimal CpuPercent { get; set; }
    public decimal MemoryPercent { get; set; }
    public decimal DiskPercent { get; set; }
    public int QueueLength { get; set; }
    public int ActiveConnections { get; set; }
    public MailServerStatus Status { get; set; }
    public JsonDocument? Metrics { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public MailServer? MailServer { get; set; }
}

public class Notification : TenantEntity
{
    /// <summary>Null means broadcast to all users of the organization.</summary>
    public Guid? UserId { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public NotificationLevel Level { get; set; }
    public string? Link { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Installation-wide configuration values. Never holds secrets.</summary>
public class SystemConfiguration : Entity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public string? Description { get; set; }
}
