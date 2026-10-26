using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MX.Application.Abstractions;
using MX.Application.Common;
using MX.Domain;
using MX.Domain.Entities;
using MX.Persistence;

namespace MX.MailServer.Submission;

/// <summary>A mail client that has logged in, and the addresses it may send as.</summary>
public sealed record AuthenticatedMailbox(Guid OrganizationId, Guid MailboxId, string Address, IReadOnlySet<string> AllowedSenders);

public enum AuthOutcome { Success, InvalidCredentials, Throttled }

public interface IMailboxAuthenticator
{
    /// <param name="protocol">Shown in the sign-in log, e.g. "SMTP submission" or "IMAP".</param>
    Task<(AuthOutcome Outcome, AuthenticatedMailbox? Mailbox)> AuthenticateAsync(string username, string password, string remoteIp, string protocol, CancellationToken ct);
}

/// <summary>
/// Verifies mailbox credentials (Argon2id) with brute-force protection per IP and per account.
/// Every attempt is recorded in authentication_events so it shows under Security → sign-ins.
/// </summary>
public sealed class MailboxAuthenticator(IServiceScopeFactory scopes, IPasswordHasher hasher, AuthThrottle throttle) : IMailboxAuthenticator
{
    // Argon2id hash of a random value: keeps timing similar for unknown accounts.
    private const string DummyHash = "$argon2id$v=19$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public async Task<(AuthOutcome, AuthenticatedMailbox?)> AuthenticateAsync(string username, string password, string remoteIp, string protocol, CancellationToken ct)
    {
        var address = username.Trim().ToLowerInvariant();
        if (throttle.IsBlocked(remoteIp, address)) return (AuthOutcome.Throttled, null);

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope(null);
        var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();

        var mailbox = await db.Mailboxes
            .Where(m => m.EmailAddress == address && m.Status == MailboxStatus.Active && m.PasswordHash != null)
            .Select(m => new { m.Id, m.OrganizationId, m.EmailAddress, m.PasswordHash })
            .FirstOrDefaultAsync(ct);

        var valid = hasher.Verify(password, mailbox?.PasswordHash ?? DummyHash) && mailbox is not null;
        db.AuthenticationEvents.Add(new AuthenticationEvent
        {
            OrganizationId = mailbox?.OrganizationId,
            AttemptedEmail = address.Length <= 320 ? address : address[..320],
            Outcome = valid ? AuthenticationOutcome.Success : AuthenticationOutcome.InvalidCredentials,
            IpAddress = remoteIp,
            UserAgent = $"mail client ({protocol})",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        if (!valid)
        {
            await db.SaveChangesAsync(ct);
            throttle.RecordFailure(remoteIp, address);
            return (AuthOutcome.InvalidCredentials, null);
        }

        await db.Mailboxes.Where(m => m.Id == mailbox!.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastLoginAt, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        throttle.RecordSuccess(remoteIp, address);

        var aliases = await db.MailAliases.Where(a => a.MailboxId == mailbox!.Id).Select(a => a.AliasAddress).ToListAsync(ct);
        var allowed = new HashSet<string>(aliases.Append(mailbox!.EmailAddress), StringComparer.OrdinalIgnoreCase);
        return (AuthOutcome.Success, new AuthenticatedMailbox(mailbox.OrganizationId, mailbox.Id, mailbox.EmailAddress, allowed));
    }
}

/// <summary>
/// Sliding-window counters for failed logins. In-memory, so limits apply per mail host process;
/// with several hosts each enforces its own window (documented limitation).
/// </summary>
public sealed class AuthThrottle(IOptions<MailServerOptions> options)
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(string ip, string account)
    {
        var o = options.Value;
        return Count($"ip:{ip}") >= o.MaxAuthFailuresPerIp || Count($"acct:{account}") >= o.MaxAuthFailuresPerAccount;
    }

    public void RecordFailure(string ip, string account)
    {
        var now = DateTimeOffset.UtcNow;
        _failures.GetOrAdd($"ip:{ip}", _ => new()).Enqueue(now);
        _failures.GetOrAdd($"acct:{account}", _ => new()).Enqueue(now);
    }

    /// <summary>A successful login clears that IP's failures (not the account's: an attacker may share the IP).</summary>
    public void RecordSuccess(string ip, string account) => _failures.TryRemove($"ip:{ip}", out _);

    private int Count(string key)
    {
        if (!_failures.TryGetValue(key, out var queue)) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-options.Value.AuthFailureWindowMinutes);
        while (queue.TryPeek(out var oldest) && oldest < cutoff) queue.TryDequeue(out _);
        if (queue.IsEmpty) _failures.TryRemove(new KeyValuePair<string, ConcurrentQueue<DateTimeOffset>>(key, queue));
        return queue.Count;
    }
}
