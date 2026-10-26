using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MX.Api.Controllers;
using MX.Application.Auth;
using MX.Domain;
using MX.MailServer.Webmail;
using MX.Security;
using MX.Tests.Infrastructure;

namespace MX.Tests;

/// <summary>Webmail API: mailbox sign-in, token isolation from the admin API, sending, reading and folders.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WebmailTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Password = "Webmail-Correct-Horse-7";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), "mx-webmail-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        var hash = new Argon2PasswordHasher().Hash(Password);
        await using var ctx = db.CreateContext(db.ExampleOrgId);
        await ctx.Mailboxes.Where(m => m.EmailAddress == "jordan.lee@example.com").ExecuteUpdateAsync(s => s.SetProperty(m => m.PasswordHash, hash));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true);
        return Task.CompletedTask;
    }

    private HttpClient Client() => db.CreateApi().WithWebHostBuilder(b =>
    {
        b.UseSetting("MailStore:Root", _storeRoot);
        b.UseSetting("MailServer:Hostname", "mx.test");
        b.UseSetting("Secrets:KeyEncryptionKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
    }).CreateClient();

    private async Task<HttpClient> SignedIn()
    {
        var client = Client();
        var res = await client.PostAsJsonAsync("/api/webmail/login", new WebmailLoginRequest("jordan.lee@example.com", Password));
        res.EnsureSuccessStatusCode();
        var login = await res.Content.ReadFromJsonAsync<WebmailLoginResponse>(Json);
        Assert.Equal("jordan.lee@example.com", login!.Profile.Address);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var res = await Client().PostAsJsonAsync("/api/webmail/login", new WebmailLoginRequest("jordan.lee@example.com", "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Webmail_and_admin_tokens_do_not_cross_over()
    {
        var mail = await SignedIn();
        Assert.Equal(HttpStatusCode.Unauthorized, (await mail.GetAsync("/api/mailboxes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mail.GetAsync("/api/auth/me")).StatusCode);

        var admin = Client();
        var login = await (await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", PostgresFixture.Password))).Content.ReadFromJsonAsync<LoginResponse>(Json);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/webmail/folders")).StatusCode);
    }

    [Fact]
    public async Task Send_files_a_sent_copy_that_can_be_read_flagged_and_deleted()
    {
        var client = await SignedIn();
        var subject = "Webmail test " + Guid.NewGuid().ToString("N")[..8];
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Elena <elena.garcia@example.com>"), "to" },
            { new StringContent(subject), "subject" },
            { new StringContent("Hello from webmail"), "text" },
            { new StringContent("<p>Hello from <b>webmail</b></p>"), "html" },
        };
        var file = new ByteArrayContent("report body"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "attachments", "report.txt");
        var sent = await client.PostAsync("/api/webmail/send", form);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);

        var list = await client.GetFromJsonAsync<WebmailList>($"/api/webmail/messages?folder=Sent&search={Uri.EscapeDataString(subject)}", Json);
        var item = Assert.Single(list!.Items);
        Assert.True(item.HasAttachments);
        Assert.Equal("elena.garcia@example.com", Assert.Single(item.To).Address);
        Assert.Contains("Hello from webmail", item.Snippet);

        var message = await client.GetFromJsonAsync<WebmailMessage>($"/api/webmail/messages/{item.Id}", Json);
        Assert.Contains("<b>webmail</b>", message!.Html);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("report.txt", attachment.FileName);
        Assert.Equal("report body", await client.GetStringAsync($"/api/webmail/messages/{item.Id}/attachments/0"));

        (await client.PostAsJsonAsync("/api/webmail/messages/flags", new WebmailFlagsRequest([item.Id], null, true))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/webmail/messages/delete", new WebmailIdsRequest([item.Id]))).EnsureSuccessStatusCode();
        var trash = await client.GetFromJsonAsync<WebmailList>($"/api/webmail/messages?folder=Trash&search={Uri.EscapeDataString(subject)}", Json);
        Assert.True(Assert.Single(trash!.Items).IsFlagged);

        // Deleting from Trash removes it for good.
        (await client.PostAsJsonAsync("/api/webmail/messages/delete", new WebmailIdsRequest([item.Id]))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/webmail/messages/{item.Id}")).StatusCode);

        await using var ctx = db.CreateContext(db.ExampleOrgId);
        Assert.True(await ctx.OutboundRecipients.AnyAsync(r => r.Address == "elena.garcia@example.com" && r.OutboundMessage!.Subject == subject));
    }

    [Fact]
    public async Task Cannot_send_as_someone_else_or_to_bad_addresses()
    {
        var client = await SignedIn();
        using var spoof = new MultipartFormDataContent { { new StringContent("elena.garcia@example.com"), "to" }, { new StringContent("ceo@example.com"), "from" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/webmail/send", spoof)).StatusCode);
        using var bad = new MultipartFormDataContent { { new StringContent("not-an-address"), "to" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/webmail/send", bad)).StatusCode);
    }

    [Fact]
    public async Task Folders_are_listed_with_counts()
    {
        var folders = await (await SignedIn()).GetFromJsonAsync<List<WebmailFolder>>("/api/webmail/folders", Json);
        Assert.Equal([MailFolder.Inbox, MailFolder.Sent, MailFolder.Drafts, MailFolder.Junk, MailFolder.Trash, MailFolder.Archive], folders!.Select(f => f.Folder));
    }
}
