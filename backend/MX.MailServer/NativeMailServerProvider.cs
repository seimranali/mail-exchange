using DnsClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MX.Application.Abstractions;
using MX.Domain;
using MX.MailServer.Authentication;
using MX.MailServer.Delivery;
using MX.MailServer.Outbound;
using MX.MailServer.Smtp;
using MX.MailServer.Storage;
using MX.MailServer.Submission;

namespace MX.MailServer;

/// <summary>
/// Management-plane adapter for the built-in mail server. Inbound delivery reads mailbox state
/// straight from the management database; the outbound queue arrives in step 2.
/// </summary>
public sealed class NativeMailServerProvider : IMailServerProvider
{
    public MailServerType ServerType => MailServerType.Native;

    // Native mailboxes are resolved from the management database at SMTP time and storage
    // directories are created on first delivery, so lifecycle calls have nothing extra to do.
    public Task ProvisionMailboxAsync(MailboxProvisionRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task SetMailboxEnabledAsync(string emailAddress, bool enabled, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteMailboxAsync(string emailAddress, CancellationToken ct) => Task.CompletedTask;
    public Task AddAliasAsync(string aliasAddress, string targetAddress, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveAliasAsync(string aliasAddress, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Usage is maintained transactionally by LocalDelivery, so this returns -1 ("not reported")
    /// and the reconciliation worker leaves the stored value alone.
    /// </summary>
    public Task<MailboxUsage> GetMailboxUsageAsync(string emailAddress, CancellationToken ct) => Task.FromResult(new MailboxUsage(-1, -1));

    public Task<IReadOnlyList<QueuedMessage>> GetQueueAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<QueuedMessage>>([]);

    public Task<ServerHealth> GetHealthAsync(CancellationToken ct)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath("."))!);
        var diskPercent = drive.TotalSize > 0 ? Math.Round(100m * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize, 2) : 0;
        var memory = GC.GetGCMemoryInfo();
        var memPercent = memory.TotalAvailableMemoryBytes > 0 ? Math.Round(100m * memory.MemoryLoadBytes / memory.TotalAvailableMemoryBytes, 2) : 0;
        return Task.FromResult(new ServerHealth(MailServerStatus.Online, 0, memPercent, diskPercent, 0, 0));
    }
}

public static class DependencyInjection
{
    /// <summary>Services shared by the mail host and the API (store + management adapter).</summary>
    public static IServiceCollection AddNativeMailStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MessageStoreOptions>(configuration.GetSection(MessageStoreOptions.Section));
        services.Configure<MailServerOptions>(configuration.GetSection(MailServerOptions.Section));
        services.Configure<OutboundOptions>(configuration.GetSection(OutboundOptions.Section));
        services.TryAddSingleton<IMessageStore, FileMessageStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMailServerProvider, NativeMailServerProvider>());
        services.TryAddScoped<OutboundMailer>();
        services.TryAddScoped<IOutboundMailer>(sp => sp.GetRequiredService<OutboundMailer>());
        services.Configure<Filtering.FilterOptions>(configuration.GetSection(Filtering.FilterOptions.Section));
        services.TryAddSingleton<ILocalDelivery, LocalDelivery>();
        services.TryAddSingleton<Filtering.QuarantineService>();
        services.TryAddSingleton<Filtering.IQuarantine>(sp => sp.GetRequiredService<Filtering.QuarantineService>());
        services.TryAddSingleton<IQuarantineStore>(sp => sp.GetRequiredService<Filtering.QuarantineService>());
        return services;
    }

    /// <summary>Webmail for mailbox users (API process): sign-in, reading and sending through the submission path.</summary>
    public static IServiceCollection AddWebmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddNativeMailStore(configuration);
        services.AddMemoryCache();
        services.TryAddSingleton<AuthThrottle>();
        services.TryAddSingleton<IMailboxAuthenticator, MailboxAuthenticator>();
        services.TryAddSingleton<ISubmissionHandler, SubmissionHandler>();
        services.TryAddSingleton<Webmail.WebmailService>();
        return services;
    }

    /// <summary>The SMTP server itself. Only the mail host process calls this.</summary>
    public static IServiceCollection AddSmtpServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddNativeMailStore(configuration);
        services.Configure<Acme.AcmeOptions>(configuration.GetSection(Acme.AcmeOptions.Section));
        services.TryAddSingleton<Tls.CertificateProvider>();
        services.AddSingleton<ILookupClient>(_ => MX.Infrastructure.MailDnsClientFactory.Create(configuration));
        services.AddSingleton<IMailDns, DnsClientMailDns>();
        services.AddSingleton<SpfEvaluator>();
        services.AddSingleton<DkimChecker>();
        services.AddSingleton<DmarcEvaluator>();
        services.AddSingleton<IRecipientDirectory, RecipientDirectory>();
        services.AddSingleton<Filtering.IMalwareScanner, Filtering.AttachmentScanner>();
        services.AddSingleton<Filtering.IContentFilter, Filtering.ContentFilter>();
        services.AddSingleton<AuthThrottle>();
        services.AddSingleton<IMailboxAuthenticator, MailboxAuthenticator>();
        services.AddSingleton<ISubmissionHandler, SubmissionHandler>();
        services.AddSingleton<SmtpListener>();
        services.AddHostedService(sp => sp.GetRequiredService<SmtpListener>());
        return services;
    }

    /// <summary>The IMAP server. Only the mail host process calls this (after AddSmtpServer, which supplies authentication).</summary>
    public static IServiceCollection AddImapServer(this IServiceCollection services)
    {
        services.AddSingleton<Imap.ImapListener>();
        services.AddHostedService(sp => sp.GetRequiredService<Imap.ImapListener>());
        return services;
    }

    /// <summary>Automatic TLS certificates via ACME (Let's Encrypt). Only the mail host process calls this.</summary>
    public static IServiceCollection AddAcmeCertificates(this IServiceCollection services)
    {
        services.AddSingleton<Acme.Http01ChallengeServer>();
        services.AddHostedService(sp => sp.GetRequiredService<Acme.Http01ChallengeServer>());
        services.AddSingleton<Acme.CertificateManager>();
        services.AddHostedService(sp => sp.GetRequiredService<Acme.CertificateManager>());
        return services;
    }

    /// <summary>Outbound queue processing. Only the mail host process calls this.</summary>
    public static IServiceCollection AddOutboundDelivery(this IServiceCollection services)
    {
        services.AddSingleton<SmtpDeliveryClient>();
        services.AddSingleton<OutboundDeliveryWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboundDeliveryWorker>());
        return services;
    }
}
