using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MX.Api.Hubs;
using MX.Api.Infrastructure;
using MX.Application;
using MX.Application.Abstractions;
using MX.Compliance;
using MX.Infrastructure;
using MX.MailIntegration;
using MX.MailServer;
using MX.Persistence;
using MX.Security;
using Scalar.AspNetCore;

// As a Windows service the working directory is System32: settings and wwwroot come from the app folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Services.AddWindowsService(o => o.ServiceName = "mx-api"); // no-op unless started by the Windows service manager

// Optional: serve HTTPS with the mail server's ACME certificate, re-read whenever it is renewed
// (Windows/macOS installs; Linux restarts the service on renewal instead).
if (builder.Configuration["Web:CertificatePath"] is { Length: > 0 } webCertPath)
{
    var webCert = new MX.Api.Infrastructure.ReloadingCertificate(webCertPath, builder.Configuration["Web:CertificatePassword"]);
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificateSelector = (_, _) => webCert.Current));
}

builder.Services
    .AddApplication()
    .AddPersistence(builder.Configuration)
    .AddMxSecurity(builder.Configuration)
    .AddInfrastructure()
    .AddMailIntegration()
    .AddNativeMailStore(builder.Configuration)
    .AddWebmail(builder.Configuration)
    .AddCompliance();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHostedService<ServerHealthRelay>();
builder.Services.AddHealthChecks().AddDbContextCheck<MxDbContext>("postgres");

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = jwt.GetKey(),
        NameClaimType = "name",
        RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        ClockSkew = TimeSpan.FromMinutes(1),
    };
    // Browsers cannot set headers on WebSocket requests, so SignalR sends the token in the query string.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var token))
                ctx.Token = token;
            return Task.CompletedTask;
        },
    };
})
// Mailbox users (webmail): same signing key, separate audience, so the two token kinds never cross over.
.AddJwtBearer(MX.Api.Controllers.WebmailController.Scheme, o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = MX.Api.Controllers.WebmailController.Audience,
        IssuerSigningKey = jwt.GetKey(),
        NameClaimType = "email",
        ClockSkew = TimeSpan.FromMinutes(1),
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// "--initialize-database": apply migrations and seed reference data with the owner role, then exit.
// Used by the Linux installer and upgrades; the running service itself uses the restricted role.
var initializeOnly = args.Contains("--initialize-database");
if (initializeOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    var owner = app.Configuration.GetConnectionString("MxOwner")
        ?? throw new InvalidOperationException("ConnectionStrings:MxOwner is required to initialize the database.");
    await DatabaseInitializer.InitializeAsync(owner, app.Services.GetRequiredService<IPasswordHasher>(),
        new SeedOptions(
            app.Configuration["Seed:AdminEmail"] ?? "admin@mx.local",
            app.Configuration["Seed:AdminPassword"] ?? throw new InvalidOperationException("Seed:AdminPassword must be configured."),
            app.Configuration.GetValue<bool>("Seed:IncludeDemoData"),
            app.Configuration["Seed:MailServerHostname"] ?? "mx-dev-01.local"),
        app.Logger);
    if (initializeOnly) return;
}

// Behind a reverse proxy (nginx) on the same host: trust X-Forwarded-* from loopback only, so the
// real client IP reaches audit logs and rate limiting, and HTTPS redirects don't loop.
if (app.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
{
    var forwarded = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    forwarded.KnownProxies.Add(System.Net.IPAddress.Loopback);
    forwarded.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Browser hardening for the admin UI served from this host.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

// The built admin UI (frontend/dist) is served from wwwroot when present. Static files must run
// before routing: otherwise the SPA fallback route matches /assets/*.js first and the static-file
// middleware skips requests that already have an endpoint, so scripts come back as index.html.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Versioned /assets/* never change: cache for a year. Everything else (index.html,
    // theme-init.js) must be revalidated so a new release is picked up immediately.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
});
app.UseRouting();

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapHealthChecks("/health");
// Client-side routes (e.g. /mailboxes) load the SPA; unknown /api and /hubs paths still 404.
app.MapFallbackToFile("{*path:regex(^(?!api/|hubs/).*$)}", "index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();

public partial class Program;
