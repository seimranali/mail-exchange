using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MX.Application.Auth;
using MX.Application.Common;
using MX.Application.Mail;
using MX.Tests.Infrastructure;

namespace MX.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiTests(PostgresFixture db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> ClientFor(string email)
    {
        var client = db.CreateApi().CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, PostgresFixture.Password));
        res.EnsureSuccessStatusCode();
        var login = await res.Content.ReadFromJsonAsync<LoginResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Admin_ui_static_files_are_served_with_correct_types_and_spa_fallback_works()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "mx-wwwroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>MX Admin</title>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app-123.js"), "console.log('ok')");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app-123.css"), "body{}");
        try
        {
            var client = db.CreateApi().WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();

            var js = await client.GetAsync("/assets/app-123.js");
            Assert.Equal(HttpStatusCode.OK, js.StatusCode);
            Assert.Equal("text/javascript", js.Content.Headers.ContentType?.MediaType);
            Assert.Equal("text/css", (await client.GetAsync("/assets/app-123.css")).Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", js.Headers.CacheControl!.ToString());

            var deepLink = await client.GetAsync("/mailboxes"); // client-side route -> SPA shell
            Assert.Equal("text/html", deepLink.Content.Headers.ContentType?.MediaType);
            Assert.Contains("MX Admin", await deepLink.Content.ReadAsStringAsync());
            Assert.True(deepLink.Headers.CacheControl!.NoCache); // the shell is always revalidated

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode); // APIs never get the shell
            Assert.Equal("DENY", deepLink.Headers.GetValues("X-Frame-Options").Single());
        }
        finally
        {
            Directory.Delete(webRoot, true);
        }
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var client = db.CreateApi().CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/mailboxes")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_returns_401_and_is_recorded()
    {
        var client = db.CreateApi().CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", "nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Org_admin_can_list_and_create_mailboxes()
    {
        var client = await ClientFor("admin@example.com");
        var domains = await client.GetFromJsonAsync<PagedResult<DomainDto>>("/api/domains", Json);
        var domain = domains!.Items.First(d => d.DomainName == "example.com");

        var local = $"t{Guid.NewGuid():N}"[..12];
        var res = await client.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest(domain.Id, local, "Test User"), Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<MailboxDto>(Json);
        Assert.Equal($"{local}@example.com", created!.EmailAddress);

        var dup = await client.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest(domain.Id, local, "Dup"), Json);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Role_without_mail_permissions_cannot_create_mailboxes()
    {
        var client = await ClientFor("security@example.com");
        var res = await client.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest(Guid.NewGuid(), "x", "x"), Json);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_mailbox_by_id()
    {
        var exampleClient = await ClientFor("admin@example.com");
        var mailboxes = await exampleClient.GetFromJsonAsync<PagedResult<MailboxDto>>("/api/mailboxes", Json);
        var victimId = mailboxes!.Items[0].Id;

        // Platform admin acting inside the platform organization is just another tenant here.
        var other = await ClientFor("admin@mx.local");
        other.DefaultRequestHeaders.Add("X-MX-Organization", db.PlatformOrgId.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mailboxes/{victimId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/mailboxes/{victimId}")).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<PagedResult<MailboxDto>>("/api/mailboxes", Json))!.TotalCount);
    }

    [Fact]
    public async Task Org_admin_sees_only_their_own_organization()
    {
        var client = await ClientFor("admin@example.com");
        var orgs = await client.GetFromJsonAsync<PagedResult<JsonElement>>("/api/organizations", Json);
        Assert.Equal(1, orgs!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/organizations/{db.PlatformOrgId}")).StatusCode);
    }
}
