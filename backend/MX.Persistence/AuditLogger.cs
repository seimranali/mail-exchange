using System.Text.Json;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain.Entities;

namespace MX.Persistence;

/// <summary>
/// Stages an audit event on the current unit of work, so it commits atomically with the change it describes.
/// </summary>
public sealed class AuditLogger(MxDbContext db, TenantContext tenant, IClock clock) : IAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(string action, string entityType, string? entityId, object? details = null, bool privileged = false)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            OrganizationId = tenant.OrganizationId,
            ActorUserId = tenant.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = tenant.ClientIpAddress,
            IsPrivileged = privileged || tenant.IsPlatformScope,
            Details = details is null ? null : JsonSerializer.SerializeToDocument(details, JsonOptions),
            OccurredAt = clock.UtcNow,
        });
    }
}
