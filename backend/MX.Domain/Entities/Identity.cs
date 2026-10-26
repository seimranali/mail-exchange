using MX.Domain.Common;

namespace MX.Domain.Entities;

public class ApplicationUser : TenantEntity
{
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>Argon2id hash in PHC string format. Plaintext is never stored.</summary>
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MfaEnabled { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndsAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
}

/// <summary>System-wide role definitions (e.g. PlatformAdmin, OrgAdmin).</summary>
public class ApplicationRole : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsPlatformRole { get; set; }
}

public class UserRole : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public ApplicationUser? User { get; set; }
    public ApplicationRole? Role { get; set; }
}

public class ApiCredential : TenantEntity
{
    public required string Name { get; set; }
    /// <summary>Short public prefix used to look the key up.</summary>
    public required string KeyPrefix { get; set; }
    /// <summary>SHA-256 hash of the full key.</summary>
    public required string KeyHash { get; set; }
    public string[] Scopes { get; set; } = [];
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
