using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MX.MailServer.Acme;

public sealed class AcmeOptions
{
    public const string Section = "Acme";

    public bool Enabled { get; set; }
    /// <summary>Let's Encrypt staging by default; switch to production once the setup works.</summary>
    public string DirectoryUrl { get; set; } = "https://acme-staging-v02.api.letsencrypt.org/directory";
    public string? Email { get; set; }
    /// <summary>Must be set to true to accept the CA's subscriber agreement.</summary>
    public bool AcceptTermsOfService { get; set; }
    /// <summary>Names on the certificate, e.g. mail.example.com.</summary>
    public string[] Domains { get; set; } = [];
    /// <summary>Port for HTTP-01 validation. Must be reachable from the internet as port 80.</summary>
    public int HttpPort { get; set; } = 80;
    public string ListenAddress { get; set; } = "0.0.0.0";
    /// <summary>Encrypted ACME account key (created on first run).</summary>
    public string AccountKeyPath { get; set; } = "acme-account.key";
    public int RenewBeforeDays { get; set; } = 30;
    /// <summary>Tests only: accept the ACME server's self-signed TLS certificate (Pebble).</summary>
    public bool AcceptInvalidServerCertificate { get; set; }
}

public sealed class AcmeException(string message) : Exception(message);

/// <summary>
/// Minimal RFC 8555 client: ES256-signed JWS requests, nonce handling (with badNonce retry),
/// account, order, HTTP-01 challenge, finalize and certificate download.
/// </summary>
public sealed class AcmeClient(HttpClient http, ECDsa accountKey, Uri directoryUrl)
{
    private JsonObject? _directory;
    private string? _nonce;
    private string? _accountUrl;

    public string Thumbprint { get; } = ComputeThumbprint(accountKey);

    public string KeyAuthorization(string token) => $"{token}.{Thumbprint}";

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(directoryUrl, ct);
        response.EnsureSuccessStatusCode();
        _directory = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
    }

    private string DirectoryUrl(string name) => _directory?[name]?.GetValue<string>() ?? throw new AcmeException($"ACME directory has no '{name}'");

    public async Task<string> RegisterAccountAsync(string? email, bool agreeToTerms, CancellationToken ct)
    {
        if (!agreeToTerms) throw new AcmeException("The CA's terms of service must be accepted (Acme:AcceptTermsOfService).");
        var payload = new JsonObject { ["termsOfServiceAgreed"] = true };
        if (!string.IsNullOrWhiteSpace(email)) payload["contact"] = new JsonArray($"mailto:{email}");
        var (response, _) = await PostAsync(DirectoryUrl("newAccount"), payload, useJwk: true, ct);
        // 201 = created, 200 = an account already exists for this key.
        _accountUrl = response.Headers.Location?.ToString() ?? throw new AcmeException("newAccount returned no account URL");
        return _accountUrl;
    }

    public async Task<(string OrderUrl, JsonObject Order)> NewOrderAsync(IEnumerable<string> domains, CancellationToken ct)
    {
        var identifiers = new JsonArray(domains.Select(d => (JsonNode)new JsonObject { ["type"] = "dns", ["value"] = d }).ToArray());
        var (response, body) = await PostAsync(DirectoryUrl("newOrder"), new JsonObject { ["identifiers"] = identifiers }, useJwk: false, ct);
        return (response.Headers.Location?.ToString() ?? throw new AcmeException("newOrder returned no order URL"), body!);
    }

    public async Task<JsonObject> GetAsync(string url, CancellationToken ct) => (await PostAsync(url, null, useJwk: false, ct)).Body!;

    public async Task RespondToChallengeAsync(string challengeUrl, CancellationToken ct) => await PostAsync(challengeUrl, new JsonObject(), useJwk: false, ct);

    /// <summary>Polls an authorization or order until it leaves the pending/processing states.</summary>
    public async Task<JsonObject> PollAsync(string url, CancellationToken ct, int attempts = 30)
    {
        for (var i = 0; i < attempts; i++)
        {
            var obj = await GetAsync(url, ct);
            var status = obj["status"]?.GetValue<string>();
            if (status is "valid" or "invalid" or "deactivated" or "expired" or "revoked") return obj;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + i, 5)), ct);
        }
        throw new AcmeException($"Timed out waiting for {url}");
    }

    public async Task<JsonObject> FinalizeAsync(string finalizeUrl, byte[] csrDer, CancellationToken ct) =>
        (await PostAsync(finalizeUrl, new JsonObject { ["csr"] = Base64Url(csrDer) }, useJwk: false, ct)).Body!;

    public async Task<string> DownloadCertificateAsync(string certificateUrl, CancellationToken ct)
    {
        var (response, _) = await PostAsync(certificateUrl, null, useJwk: false, ct, accept: "application/pem-certificate-chain", parseJson: false);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> PostAsync(string url, JsonObject? payload, bool useJwk, CancellationToken ct,
        string accept = "application/json", bool parseJson = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            var nonce = _nonce ?? await NewNonceAsync(ct);
            _nonce = null;
            var header = new JsonObject { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url };
            if (useJwk || _accountUrl is null) header["jwk"] = Jwk(accountKey);
            else header["kid"] = _accountUrl;

            var protectedB64 = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
            var payloadB64 = payload is null ? "" : Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString())); // "" = POST-as-GET
            var signature = accountKey.SignData(Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var jws = new JsonObject { ["protected"] = protectedB64, ["payload"] = payloadB64, ["signature"] = Base64Url(signature) };

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(jws.ToJsonString(), Encoding.UTF8) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            request.Headers.Accept.ParseAdd(accept);
            var response = await http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Replay-Nonce", out var nonces)) _nonce = nonces.FirstOrDefault();

            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadAsStringAsync(ct);
                var type = TryGet(problem, "type");
                // badNonce is expected occasionally (RFC 8555 section 6.5); retry with the fresh nonce.
                if (type == "urn:ietf:params:acme:error:badNonce" && attempt < 5) { response.Dispose(); continue; }
                response.Dispose();
                throw new AcmeException($"ACME request to {url} failed ({(int)response.StatusCode}): {TryGet(problem, "detail") ?? problem}");
            }
            if (!parseJson) return (response, null);
            var text = await response.Content.ReadAsStringAsync(ct);
            return (response, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
        }
    }

    private async Task<string> NewNonceAsync(CancellationToken ct)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, DirectoryUrl("newNonce")), ct);
        return response.Headers.TryGetValues("Replay-Nonce", out var values) ? values.First() : throw new AcmeException("No Replay-Nonce from newNonce");
    }

    private static string? TryGet(string json, string property)
    {
        try
        {
            return JsonNode.Parse(json)?[property]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject Jwk(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = Base64Url(p.Q.X!), ["y"] = Base64Url(p.Q.Y!) };
    }

    /// <summary>RFC 7638 thumbprint: SHA-256 over the canonical JWK (members in lexicographic order, no whitespace).</summary>
    private static string ComputeThumbprint(ECDsa key)
    {
        var p = key.ExportParameters(false);
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(p.Q.X!)}\",\"y\":\"{Base64Url(p.Q.Y!)}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
