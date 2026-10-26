using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MX.Application.Abstractions;
using MX.Application.Common;

namespace MX.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Mx")
            ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:Mx' is not configured.");

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TenantSessionInterceptor>();

        // Context pooling is deliberately not used: the context holds request-scoped tenant state.
        services.AddDbContext<MxDbContext>((sp, options) => Configure(options, connectionString)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<MxDbContext>());
        services.AddScoped<IAuditLogger, AuditLogger>();
        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) => options
        .UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable("__ef_migrations_history", "mx")
            .EnableRetryOnFailure(3))
        .UseSnakeCaseNamingConvention();
}

/// <summary>Used by `dotnet ef` at design time only.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MxDbContext>
{
    public MxDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("MX_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=mx;Username=mx_owner;Password=mx_owner_dev";
        var options = DependencyInjection.Configure(new DbContextOptionsBuilder<MxDbContext>(), cs).Options;
        var tenant = new TenantContext();
        tenant.SetPlatformScope(null);
        return new MxDbContext((DbContextOptions<MxDbContext>)options, tenant, new SystemClock());
    }
}
