using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MX.Application.Abstractions;
using MX.Application.Auth;
using MX.Application.Common;
using MX.Application.Compliance;
using MX.Application.Dashboard;
using MX.Application.Mail;
using MX.Application.Organizations;
using MX.Application.Security;
using MX.Domain;

namespace MX.Api.Controllers;

internal static class RoleSets
{
    public const string Any = $"{Roles.PlatformAdmin},{Roles.OrgAdmin},{Roles.MailAdmin},{Roles.SecurityAdmin},{Roles.ComplianceOfficer},{Roles.Auditor}";
    public const string MailManage = $"{Roles.PlatformAdmin},{Roles.OrgAdmin},{Roles.MailAdmin}";
    public const string OrgManage = $"{Roles.PlatformAdmin},{Roles.OrgAdmin}";
    public const string SecurityManage = $"{Roles.PlatformAdmin},{Roles.OrgAdmin},{Roles.SecurityAdmin}";
    public const string SecurityRead = $"{SecurityManage},{Roles.Auditor}";
    public const string ComplianceManage = $"{Roles.PlatformAdmin},{Roles.OrgAdmin},{Roles.ComplianceOfficer}";
    public const string ComplianceRead = $"{ComplianceManage},{Roles.Auditor}";
}

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth, ITenantContextSetter tenant) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<LoginResponse> Login(LoginRequest request, CancellationToken ct)
    {
        // Sign-in precedes tenant resolution; the service only reads the matching user.
        tenant.SetPlatformScope(null);
        return await auth.LoginAsync(request, new LoginContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent), ct);
    }

    [HttpGet("me")]
    [Authorize]
    public Task<CurrentUserDto> Me(CancellationToken ct) =>
        auth.GetCurrentAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!), ct);
}

[ApiController]
[Route("api/dashboard")]
[Authorize(Roles = RoleSets.Any)]
public sealed class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public Task<DashboardSummary> Get(CancellationToken ct) => dashboard.GetAsync(ct);
}

[ApiController]
[Route("api/organizations")]
[Authorize(Roles = RoleSets.OrgManage)]
public sealed class OrganizationsController(OrganizationService organizations) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<OrganizationDto>> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        organizations.ListAsync(new PageRequest(page, pageSize), search, ct);

    [HttpGet("{id:guid}")]
    public Task<OrganizationDto> Get(Guid id, CancellationToken ct) => organizations.GetAsync(id, ct);

    [HttpPost]
    [Authorize(Roles = Roles.PlatformAdmin)]
    public async Task<ActionResult<OrganizationDto>> Create(CreateOrganizationRequest request, CancellationToken ct)
    {
        var created = await organizations.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public Task<OrganizationDto> Update(Guid id, UpdateOrganizationRequest request, CancellationToken ct) => organizations.UpdateAsync(id, request, ct);
}

[ApiController]
[Route("api/domains")]
[Authorize(Roles = RoleSets.MailManage)]
public sealed class DomainsController(DomainService domains) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = RoleSets.Any)]
    public Task<PagedResult<DomainDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        domains.ListAsync(new PageRequest(page, pageSize), ct);

    [HttpGet("{id:guid}")]
    [Authorize(Roles = RoleSets.Any)]
    public Task<DomainDto> Get(Guid id, CancellationToken ct) => domains.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<DomainDto>> Create(CreateDomainRequest request, CancellationToken ct)
    {
        var created = await domains.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPost("{id:guid}/verify")]
    public Task<DomainDto> Verify(Guid id, CancellationToken ct) => domains.VerifyAsync(id, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await domains.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/dkim")]
    public async Task<ActionResult<DkimKeyDto>> GetDkim(Guid id, [FromServices] DkimKeyService dkim, CancellationToken ct) =>
        await dkim.GetActiveAsync(id, ct) is { } key ? key : NotFound();

    /// <summary>Generates a new DKIM key (rotating out the old one) and returns the DNS record to publish.</summary>
    [HttpPost("{id:guid}/dkim")]
    public Task<DkimKeyDto> RotateDkim(Guid id, [FromServices] DkimKeyService dkim, CancellationToken ct) => dkim.RotateAsync(id, ct);
}

public sealed record AddAliasRequest(string LocalPart, Guid DomainId);

[ApiController]
[Route("api/mailboxes")]
[Authorize(Roles = RoleSets.MailManage)]
public sealed class MailboxesController(MailboxService mailboxes) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = RoleSets.Any)]
    public Task<PagedResult<MailboxDto>> List(
        [FromQuery] string? search, [FromQuery] MailboxStatus? status, [FromQuery] MailboxType? type, [FromQuery] Guid? domainId,
        [FromQuery] string? sort, [FromQuery] bool desc = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        mailboxes.ListAsync(new MailboxQuery(search, status, type, domainId, sort, desc), new PageRequest(page, pageSize), ct);

    [HttpGet("{id:guid}")]
    [Authorize(Roles = RoleSets.Any)]
    public Task<MailboxDto> Get(Guid id, CancellationToken ct) => mailboxes.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<MailboxDto>> Create(CreateMailboxRequest request, CancellationToken ct)
    {
        var created = await mailboxes.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public Task<MailboxDto> Update(Guid id, UpdateMailboxRequest request, CancellationToken ct) => mailboxes.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await mailboxes.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/aliases")]
    public Task<MailboxDto> AddAlias(Guid id, AddAliasRequest request, CancellationToken ct) => mailboxes.AddAliasAsync(id, request.LocalPart, request.DomainId, ct);

    /// <summary>Received-message metadata for troubleshooting delivery. Audited; never returns content.</summary>
    [HttpGet("{id:guid}/messages")]
    public Task<PagedResult<MessageSummaryDto>> Messages(Guid id, [FromServices] MessageService messages, [FromQuery] MailFolder? folder,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        messages.ListAsync(id, folder, new PageRequest(page, pageSize), ct);

    /// <summary>Sets the mail-client password (null disables client login).</summary>
    [HttpPut("{id:guid}/client-password")]
    public async Task<IActionResult> SetClientPassword(Guid id, SetClientPasswordRequest request, CancellationToken ct)
    {
        await mailboxes.SetClientPasswordAsync(id, request.Password, ct);
        return NoContent();
    }

    /// <summary>Sends a test message from this mailbox through the outbound queue.</summary>
    [HttpPost("{id:guid}/test-message")]
    public async Task<ActionResult<object>> SendTestMessage(Guid id, SendTestMessageRequest request, [FromServices] OutboundQueueService queue, CancellationToken ct) =>
        Accepted(new { outboundMessageId = await queue.SendTestMessageAsync(id, request.To, ct) });
}

public sealed record SendTestMessageRequest(string To);
public sealed record SetClientPasswordRequest(string? Password);

public sealed record UpdateAlertStatusRequest(AlertStatus Status);

[ApiController]
[Route("api/security")]
[Authorize(Roles = RoleSets.SecurityManage)]
public sealed class SecurityController(SecurityService security) : ControllerBase
{
    [HttpGet("alerts")]
    [Authorize(Roles = RoleSets.SecurityRead)]
    public Task<PagedResult<SecurityAlertDto>> Alerts([FromQuery] AlertStatus? status, [FromQuery] AlertSeverity? severity,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        security.ListAlertsAsync(status, severity, new PageRequest(page, pageSize), ct);

    [HttpPut("alerts/{id:guid}/status")]
    public async Task<IActionResult> UpdateAlert(Guid id, UpdateAlertStatusRequest request, CancellationToken ct)
    {
        await security.UpdateAlertStatusAsync(id, request.Status, ct);
        return NoContent();
    }

    [HttpGet("quarantine")]
    [Authorize(Roles = RoleSets.SecurityRead)]
    public Task<PagedResult<QuarantineDto>> Quarantine([FromQuery] QuarantineStatus? status, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        security.ListQuarantineAsync(status, search, new PageRequest(page, pageSize), ct);

    [HttpPost("quarantine/{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        await security.ReleaseQuarantineAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("quarantine/{id:guid}")]
    public async Task<IActionResult> DeleteQuarantine(Guid id, CancellationToken ct)
    {
        await security.DeleteQuarantineAsync(id, ct);
        return NoContent();
    }

    [HttpGet("policy")]
    [Authorize(Roles = RoleSets.SecurityRead)]
    public Task<SecurityPolicyDto> Policy(CancellationToken ct) => security.GetPolicyAsync(ct);

    [HttpPut("policy")]
    public Task<SecurityPolicyDto> UpdatePolicy(SecurityPolicyDto request, CancellationToken ct) => security.UpdatePolicyAsync(request, ct);

    [HttpGet("sign-ins")]
    [Authorize(Roles = RoleSets.SecurityRead)]
    public Task<PagedResult<AuthenticationEventDto>> SignIns([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        security.ListAuthenticationEventsAsync(new PageRequest(page, pageSize), ct);
}

[ApiController]
[Route("api/compliance")]
[Authorize(Roles = RoleSets.ComplianceManage)]
public sealed class ComplianceController(ComplianceService compliance) : ControllerBase
{
    [HttpGet("retention-policies")]
    [Authorize(Roles = RoleSets.ComplianceRead)]
    public Task<IReadOnlyList<RetentionPolicyDto>> RetentionPolicies(CancellationToken ct) => compliance.ListRetentionPoliciesAsync(ct);

    [HttpPost("retention-policies")]
    public Task<RetentionPolicyDto> CreateRetentionPolicy(SaveRetentionPolicyRequest request, CancellationToken ct) => compliance.SaveRetentionPolicyAsync(null, request, ct);

    [HttpPut("retention-policies/{id:guid}")]
    public Task<RetentionPolicyDto> UpdateRetentionPolicy(Guid id, SaveRetentionPolicyRequest request, CancellationToken ct) => compliance.SaveRetentionPolicyAsync(id, request, ct);

    [HttpGet("legal-holds")]
    [Authorize(Roles = RoleSets.ComplianceRead)]
    public Task<IReadOnlyList<LegalHoldDto>> LegalHolds(CancellationToken ct) => compliance.ListLegalHoldsAsync(ct);

    [HttpPost("legal-holds")]
    public Task<LegalHoldDto> CreateLegalHold(CreateLegalHoldRequest request, CancellationToken ct) => compliance.CreateLegalHoldAsync(request, ct);

    [HttpPost("legal-holds/{id:guid}/release")]
    public async Task<IActionResult> ReleaseLegalHold(Guid id, CancellationToken ct)
    {
        await compliance.ReleaseLegalHoldAsync(id, ct);
        return NoContent();
    }

    [HttpGet("audit-events")]
    [Authorize(Roles = RoleSets.ComplianceRead + "," + Roles.SecurityAdmin)]
    public Task<PagedResult<AuditEventDto>> AuditEvents([FromQuery] string? action, [FromQuery] bool? privilegedOnly,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        compliance.ListAuditEventsAsync(action, privilegedOnly, from, to, new PageRequest(page, pageSize), ct);
}

[ApiController]
[Route("api/mail-queue")]
[Authorize(Roles = RoleSets.MailManage)]
public sealed class MailQueueController(IApplicationDbContext db, ITenantContext tenant, IMailServerProviderFactory providers, OutboundQueueService nativeQueue) : ControllerBase
{
    /// <summary>
    /// Outbound queue. The native server's queue is tenant-scoped data in the management database;
    /// external MTAs are read live through their providers and filtered to the caller's domains.
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<QueuedMessage>> Get([FromQuery] int limit = 100, [FromQuery] bool includeFinished = false, CancellationToken ct = default)
    {
        var servers = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.MailServers.Where(s => s.Status != MailServerStatus.Offline).Select(s => s.ServerType).Distinct(), ct);
        var native = servers.Remove(MailServerType.Native) ? await nativeQueue.ListAsync(limit, includeFinished, ct) : [];
        var results = new List<QueuedMessage>();
        foreach (var type in servers) results.AddRange(await providers.Get(type).GetQueueAsync(Math.Clamp(limit, 1, 500), ct));
        if (results.Count == 0) return native;
        if (tenant.IsPlatformScope) return [.. native, .. results];

        // External mail servers are shared: only show messages to or from the caller's own domains.
        var domains = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.OrganizationDomains.Select(d => d.DomainName), ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static string DomainOf(string address) => address[(address.LastIndexOf('@') + 1)..];
        return [.. native, .. results.Where(m => domains.Contains(DomainOf(m.Recipient)) || domains.Contains(DomainOf(m.Sender)))];
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        await nativeQueue.RetryNowAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await nativeQueue.CancelAsync(id, ct);
        return NoContent();
    }
}
