using Microsoft.EntityFrameworkCore;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;

namespace MX.Application.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, CurrentUserDto User);
public sealed record CurrentUserDto(Guid Id, string Email, string DisplayName, Guid OrganizationId, string OrganizationName, IReadOnlyList<string> Roles);
public sealed record LoginContext(string? IpAddress, string? UserAgent);

public sealed class AuthService(IApplicationDbContext db, IPasswordHasher hasher, IAccessTokenService tokens, IClock clock)
{
    /// <summary>
    /// Sign-in runs before any tenant is known, so it reads users across tenants.
    /// Callers must run this under platform scope; nothing else here is tenant-sensitive.
    /// </summary>
    public async Task<LoginResponse> LoginAsync(LoginRequest request, LoginContext context, CancellationToken ct)
    {
        var normalized = request.Email.Trim().ToUpperInvariant();
        var user = await db.ApplicationUsers.IgnoreQueryFilters()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        var now = clock.UtcNow;
        AuthenticationOutcome outcome;
        if (user is null)
        {
            hasher.Verify(request.Password, DummyHash); // keep timing similar for unknown users
            outcome = AuthenticationOutcome.InvalidCredentials;
        }
        else if (!user.IsActive) outcome = AuthenticationOutcome.Disabled;
        else if (user.LockoutEndsAt > now) outcome = AuthenticationOutcome.LockedOut;
        else if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            outcome = AuthenticationOutcome.InvalidCredentials;
            var policy = await db.SecurityPolicies.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.OrganizationId == user.OrganizationId, ct);
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= (policy?.MaxFailedLogins ?? 5))
            {
                user.LockoutEndsAt = now.AddMinutes(policy?.LockoutMinutes ?? 15);
                user.FailedLoginCount = 0;
            }
        }
        else outcome = AuthenticationOutcome.Success;

        db.AuthenticationEvents.Add(new AuthenticationEvent
        {
            OrganizationId = user?.OrganizationId,
            UserId = user?.Id,
            AttemptedEmail = request.Email.Trim()[..Math.Min(request.Email.Trim().Length, 320)],
            Outcome = outcome,
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent?[..Math.Min(context.UserAgent.Length, 512)],
            OccurredAt = now,
        });

        if (outcome != AuthenticationOutcome.Success)
        {
            await db.SaveChangesAsync(ct);
            throw new AuthenticationFailedException(outcome == AuthenticationOutcome.LockedOut
                ? "The account is temporarily locked. Try again later."
                : "The email or password is incorrect.");
        }

        user!.FailedLoginCount = 0;
        user.LockoutEndsAt = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct);

        var roles = user.UserRoles.Select(r => r.Role!.Name).ToList();
        var token = tokens.Issue(user, roles, roles.Contains(Roles.PlatformAdmin));
        var orgName = await db.Organizations.IgnoreQueryFilters().Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstAsync(ct);
        return new LoginResponse(token.Token, token.ExpiresAt, new CurrentUserDto(user.Id, user.Email, user.DisplayName, user.OrganizationId, orgName, roles));
    }

    public async Task<CurrentUserDto> GetCurrentAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.ApplicationUsers.IgnoreQueryFilters().AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User", userId);
        var orgName = await db.Organizations.IgnoreQueryFilters().Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstAsync(ct);
        return new CurrentUserDto(user.Id, user.Email, user.DisplayName, user.OrganizationId, orgName, user.UserRoles.Select(r => r.Role!.Name).ToList());
    }

    // Argon2id hash of a random value; used only to equalize timing.
    private const string DummyHash = "$argon2id$v=19$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
}

public sealed class AuthenticationFailedException(string message) : Common.ApplicationException(message);
