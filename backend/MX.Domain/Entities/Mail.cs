using MX.Domain.Common;

namespace MX.Domain.Entities;

/// <summary>A mail server managed by this installation. Shared infrastructure, not tenant-owned.</summary>
public class MailServer : Entity
{
    public required string Name { get; set; }
    public required string Hostname { get; set; }
    public MailServerType ServerType { get; set; }
    public MailServerStatus Status { get; set; } = MailServerStatus.Online;
    public string? Region { get; set; }
    /// <summary>Reference into the secret store; never the secret itself.</summary>
    public string? CredentialSecretRef { get; set; }
}

public class Mailbox : TenantEntity
{
    public Guid DomainId { get; set; }
    public Guid? MailServerId { get; set; }
    public required string LocalPart { get; set; }
    public required string EmailAddress { get; set; }
    public required string DisplayName { get; set; }
    public MailboxType MailboxType { get; set; } = MailboxType.User;
    public MailboxStatus Status { get; set; } = MailboxStatus.Provisioning;
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>Argon2id hash for mail-client login (submission/IMAP). Null disables client login.</summary>
    public string? PasswordHash { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }

    public OrganizationDomain? Domain { get; set; }
    public MailServer? MailServer { get; set; }
}

public class MailAlias : TenantEntity
{
    public Guid MailboxId { get; set; }
    public required string AliasAddress { get; set; }
    public Mailbox? Mailbox { get; set; }
}

public class DistributionList : TenantEntity
{
    public Guid DomainId { get; set; }
    public required string Name { get; set; }
    public required string EmailAddress { get; set; }
    public string? Description { get; set; }
    public bool AllowExternalSenders { get; set; }
    public ICollection<DistributionListMember> Members { get; set; } = [];
}

public class DistributionListMember : TenantEntity
{
    public Guid DistributionListId { get; set; }
    /// <summary>Internal member, or null when <see cref="ExternalAddress"/> is used.</summary>
    public Guid? MailboxId { get; set; }
    public string? ExternalAddress { get; set; }
    public DistributionList? DistributionList { get; set; }
    public Mailbox? Mailbox { get; set; }
}

public class MailRoutingRule : TenantEntity
{
    public required string Name { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? SenderPattern { get; set; }
    public string? RecipientPattern { get; set; }
    public string? SubjectPattern { get; set; }
    public RoutingAction Action { get; set; }
    public string? ActionTarget { get; set; }
}
