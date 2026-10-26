using Microsoft.Extensions.DependencyInjection;
using MX.Application.Auth;
using MX.Application.Compliance;
using MX.Application.Dashboard;
using MX.Application.Mail;
using MX.Application.Organizations;
using MX.Application.Security;

namespace MX.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<AuthService>()
        .AddScoped<OrganizationService>()
        .AddScoped<DomainService>()
        .AddScoped<MailboxService>()
        .AddScoped<MessageService>()
        .AddScoped<DkimKeyService>()
        .AddScoped<OutboundQueueService>()
        .AddScoped<SecurityService>()
        .AddScoped<ComplianceService>()
        .AddScoped<DashboardService>();
}
