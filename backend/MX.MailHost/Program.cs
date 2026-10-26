using MX.MailServer;
using MX.Persistence;
using MX.Security;

// The mail server runs as its own process: it owns the public SMTP port, scales and restarts
// independently of the admin API, and keeps working if the API is down.
// As a Windows service the working directory is System32: settings must come from the app folder.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Services.AddWindowsService(o => o.ServiceName = "mx-mailhost"); // no-op unless started by the Windows service manager

builder.Services
    .AddPersistence(builder.Configuration)
    .AddMxSecurity(builder.Configuration)
    .AddSmtpServer(builder.Configuration)
    .AddImapServer()
    .AddAcmeCertificates()
    .AddOutboundDelivery();

builder.Build().Run();
