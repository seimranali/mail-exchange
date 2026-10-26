using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MX.Application.Common;
using MX.Persistence;
using MX.Security;

namespace MX.Api.Hubs;

/// <summary>Real-time channel. Each connection joins only its own organization's group.</summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    public static string OrganizationGroup(Guid organizationId) => $"org:{organizationId}";

    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirst(MxClaims.OrganizationId)?.Value, out var orgId))
            await Groups.AddToGroupAsync(Context.ConnectionId, OrganizationGroup(orgId));
        await base.OnConnectedAsync();
    }
}

/// <summary>Pushes the latest server health snapshots (written by MX.Workers) to connected clients.</summary>
public sealed class ServerHealthRelay(IServiceScopeFactory scopes, IHubContext<NotificationsHub> hub, ILogger<ServerHealthRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope(null);
                var db = scope.ServiceProvider.GetRequiredService<MxDbContext>();
                var since = DateTimeOffset.UtcNow.AddMinutes(-5);
                var latest = await db.ServerHealthSnapshots.AsNoTracking()
                    .Where(s => s.CapturedAt >= since)
                    .GroupBy(s => s.MailServerId)
                    .Select(g => g.OrderByDescending(s => s.CapturedAt).First())
                    .Select(s => new { s.MailServerId, s.Status, s.CpuPercent, s.MemoryPercent, s.DiskPercent, s.QueueLength, s.CapturedAt })
                    .ToListAsync(stoppingToken);
                if (latest.Count > 0) await hub.Clients.All.SendAsync("serverHealth", latest, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Server health relay failed");
            }
        }
    }
}
