using MX.Application.Abstractions;

namespace MX.Application.Common;

/// <summary>Scoped (per request / per unit of work) tenant context. Starts with no access at all.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    public Guid? OrganizationId { get; private set; }
    public Guid? UserId { get; private set; }
    public bool IsPlatformScope { get; private set; }
    public string? ClientIpAddress { get; set; }

    public void SetTenant(Guid organizationId, Guid? userId)
    {
        OrganizationId = organizationId;
        UserId = userId;
        IsPlatformScope = false;
    }

    public void SetPlatformScope(Guid? userId)
    {
        OrganizationId = null;
        UserId = userId;
        IsPlatformScope = true;
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
