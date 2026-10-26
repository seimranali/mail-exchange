namespace MX.Domain.Common;

/// <summary>Base type for all persisted entities. Identifiers are time-ordered UUID v7.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Marks an entity whose rows belong to exactly one organization (tenant).</summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
}
