using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MX.Application.Abstractions;
using MX.Domain;
using MX.MailServer.Submission;
using MX.MailServer.Webmail;
using MX.Security;

namespace MX.Api.Controllers;

public sealed record WebmailLoginRequest(string Email, string Password);
public sealed record WebmailLoginResponse(string AccessToken, DateTimeOffset ExpiresAt, WebmailProfile Profile);
public sealed record WebmailFlagsRequest(IReadOnlyList<Guid> Ids, bool? IsRead, bool? IsFlagged);
public sealed record WebmailMoveRequest(IReadOnlyList<Guid> Ids, MailFolder Folder);
public sealed record WebmailIdsRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Webmail for mailbox users. Tokens carry their own audience (<see cref="Audience"/>) and are only
/// accepted by the <see cref="Scheme"/> scheme, so a mailbox user can never call the admin API.
/// </summary>
[ApiController]
[Route("api/webmail")]
[Authorize(AuthenticationSchemes = Scheme)]
public sealed class WebmailController(WebmailService webmail, IMailboxAuthenticator authenticator, IOptions<JwtOptions> jwt, IClock clock) : ControllerBase
{
    public const string Scheme = "Webmail";
    public const string Audience = "mx-webmail";
    private const string MailboxClaim = "mbx";

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<WebmailLoginResponse>> Login(WebmailLoginRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (outcome, mailbox) = await authenticator.AuthenticateAsync(request.Email ?? "", request.Password ?? "", ip, "Webmail", ct);
        if (outcome == AuthOutcome.Throttled)
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts", detail: "Too many failed sign-ins. Wait a few minutes and try again.");
        if (mailbox is null)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: "Email or password is incorrect.");

        var o = jwt.Value;
        var expires = clock.UtcNow.AddHours(8);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, mailbox.MailboxId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, mailbox.Address),
                new Claim(MxClaims.OrganizationId, mailbox.OrganizationId.ToString()),
                new Claim(MailboxClaim, "true"),
            ]),
            NotBefore = clock.UtcNow.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(o.GetKey(), SecurityAlgorithms.HmacSha256),
        });
        return new WebmailLoginResponse(token, expires, await webmail.ProfileAsync(mailbox, ct));
    }

    [HttpGet("me")]
    public Task<WebmailProfile> Me(CancellationToken ct) => webmail.ProfileAsync(CurrentMailbox(), ct);

    [HttpGet("folders")]
    public Task<List<WebmailFolder>> Folders(CancellationToken ct) => webmail.FoldersAsync(CurrentMailbox(), ct);

    [HttpGet("messages")]
    public Task<WebmailList> List([FromQuery] MailFolder folder = MailFolder.Inbox, [FromQuery] string? search = null, [FromQuery] bool unreadOnly = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        webmail.ListAsync(CurrentMailbox(), folder, search, unreadOnly, page, pageSize, ct);

    [HttpGet("messages/{id:guid}")]
    public Task<WebmailMessage> Get(Guid id, CancellationToken ct) => webmail.GetAsync(CurrentMailbox(), id, ct);

    [HttpGet("messages/{id:guid}/attachments/{index:int}")]
    public async Task<IActionResult> Attachment(Guid id, int index, CancellationToken ct)
    {
        var (name, type, content) = await webmail.GetAttachmentAsync(CurrentMailbox(), id, index, ct);
        // Always a download: never render mail content as a page on this origin.
        return File(content, "application/octet-stream", name);
    }

    [HttpPost("messages/flags")]
    public async Task<IActionResult> Flags(WebmailFlagsRequest request, CancellationToken ct)
    {
        await webmail.UpdateFlagsAsync(CurrentMailbox(), request.Ids, request.IsRead, request.IsFlagged, ct);
        return NoContent();
    }

    [HttpPost("messages/move")]
    public async Task<IActionResult> Move(WebmailMoveRequest request, CancellationToken ct)
    {
        await webmail.MoveAsync(CurrentMailbox(), request.Ids, request.Folder, ct);
        return NoContent();
    }

    [HttpPost("messages/delete")]
    public async Task<IActionResult> Delete(WebmailIdsRequest request, CancellationToken ct)
    {
        await webmail.DeleteAsync(CurrentMailbox(), request.Ids, ct);
        return NoContent();
    }

    [HttpPost("send")]
    [RequestSizeLimit(40L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 40L * 1024 * 1024)]
    public async Task<ActionResult<object>> Send([FromForm] string to, [FromForm] string? cc, [FromForm] string? bcc, [FromForm] string? subject,
        [FromForm] string? text, [FromForm] string? html, [FromForm] Guid? inReplyTo, [FromForm] string? from, [FromForm] List<IFormFile>? attachments, CancellationToken ct)
    {
        var uploads = new List<WebmailUpload>();
        foreach (var file in attachments ?? [])
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            uploads.Add(new WebmailUpload(file.FileName, file.ContentType, buffer.ToArray()));
        }
        var messageId = await webmail.SendAsync(CurrentMailbox(), new WebmailSendRequest(to, cc, bcc, subject, text, html, inReplyTo, from, uploads), ct);
        return Accepted(new { messageId });
    }

    private AuthenticatedMailbox CurrentMailbox()
    {
        if (User.FindFirstValue(MailboxClaim) != "true"
            || !Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var mailboxId)
            || !Guid.TryParse(User.FindFirstValue(MxClaims.OrganizationId), out var orgId))
            throw new UnauthorizedAccessException();
        var address = User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? "";
        return new AuthenticatedMailbox(orgId, mailboxId, address, new HashSet<string>([address], StringComparer.OrdinalIgnoreCase));
    }
}
