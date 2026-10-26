using System.Security.Claims;
using MX.Application.Common;
using MX.Security;

namespace MX.Api.Infrastructure;

/// <summary>
/// Establishes the tenant scope for each request from the validated JWT. The organization
/// always comes from the token; a client cannot choose it, except platform administrators,
/// who may target one organization with the <c>X-MX-Organization</c> header.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public const string OrganizationHeader = "X-MX-Organization";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        tenant.ClientIpAddress = context.Connection.RemoteIpAddress?.ToString();
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            Guid? userId = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var uid) ? uid : null;
            var isPlatformAdmin = user.HasClaim(MxClaims.PlatformAdmin, "true");

            if (isPlatformAdmin)
            {
                if (Guid.TryParse(context.Request.Headers[OrganizationHeader], out var targetOrg))
                {
                    tenant.SetTenant(targetOrg, userId);
                    logger.LogInformation("Platform admin {UserId} acting in organization {OrganizationId}", userId, targetOrg);
                }
                else tenant.SetPlatformScope(userId);
            }
            else if (Guid.TryParse(user.FindFirstValue(MxClaims.OrganizationId), out var orgId))
            {
                tenant.SetTenant(orgId, userId);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next(context);
    }
}
