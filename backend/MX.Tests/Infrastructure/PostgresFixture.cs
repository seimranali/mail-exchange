using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MX.Application.Common;
using MX.Persistence;
using MX.Security;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MX.Tests.Infrastructure;

/// <summary>
/// One throwaway PostgreSQL per test run, provisioned exactly like production: separate owner
/// and restricted app roles, migrations applied by the owner, tests connecting as the app role.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Password = "Test!Password2026";
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6").WithDatabase("mx").Build();

    public string OwnerConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";
    public Guid ExampleOrgId { get; private set; }
    public Guid PlatformOrgId { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var admin = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            var setup = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "roles.sql"));
            await using var cmd = new NpgsqlCommand(setup, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        OwnerConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = "mx_owner", Password = "owner_test" }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = "mx_app", Password = "app_test" }.ConnectionString;

        await DatabaseInitializer.InitializeAsync(OwnerConnectionString, new Argon2PasswordHasher(),
            new SeedOptions("admin@mx.local", Password, IncludeDemoData: true), NullLogger.Instance);

        await using var db = CreateContext(platform: true);
        ExampleOrgId = await db.Organizations.Where(o => o.Slug == "example").Select(o => o.Id).SingleAsync();
        PlatformOrgId = await db.Organizations.Where(o => o.Slug == "platform").Select(o => o.Id).SingleAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A context connected as the restricted app role, with the given tenant scope.</summary>
    public MxDbContext CreateContext(Guid? organizationId = null, bool platform = false, bool ignoreFilters = false)
    {
        var tenant = new TenantContext();
        if (platform) tenant.SetPlatformScope(null);
        else if (organizationId is { } id) tenant.SetTenant(id, null);
        var options = MX.Persistence.DependencyInjection.Configure(new DbContextOptionsBuilder<MxDbContext>(), AppConnectionString)
            .AddInterceptors(new TenantSessionInterceptor(tenant)).Options;
        return new MxDbContext((DbContextOptions<MxDbContext>)options, tenant, new SystemClock());
    }

    public WebApplicationFactory<Program> CreateApi() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.UseSetting("ConnectionStrings:Mx", AppConnectionString);
        b.UseSetting("Database:MigrateOnStartup", "false");
        b.UseSetting("Jwt:SigningKey", "test-signing-key-0123456789-0123456789-abcdef");
        b.ConfigureServices(s => s.AddLogging(l => l.ClearProviders()));
    });
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
