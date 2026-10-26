using MX.Application;
using MX.Compliance;
using MX.Infrastructure;
using MX.MailIntegration;
using MX.MailServer;
using MX.Persistence;
using MX.Security;
using MX.Workers;

// As a Windows service the working directory is System32: settings must come from the app folder.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Services.AddWindowsService(o => o.ServiceName = "mx-workers"); // no-op unless started by the Windows service manager

builder.Services
    .AddApplication()
    .AddPersistence(builder.Configuration)
    .AddMxSecurity(builder.Configuration)
    .AddInfrastructure()
    .AddMailIntegration()
    .AddNativeMailStore(builder.Configuration)
    .AddCompliance();

builder.Services.AddHostedService<ServerHealthWorker>();
builder.Services.AddHostedService<MailboxReconciliationWorker>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<BackupJobWorker>();

builder.Build().Run();
