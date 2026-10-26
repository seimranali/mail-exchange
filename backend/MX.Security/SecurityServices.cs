using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MX.Application.Abstractions;
using MX.Domain.Entities;

namespace MX.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "mx";
    public string Audience { get; set; } = "mx-admin";
    /// <summary>HMAC signing key, at least 32 bytes. Supply via environment/secret store, never source control.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 60;

    public SymmetricSecurityKey GetKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        return new SymmetricSecurityKey(bytes);
    }
}

public static class MxClaims
{
    public const string OrganizationId = "org";
    public const string PlatformAdmin = "platform_admin";
}

/// <summary>Argon2id password hashing, stored in PHC string format.</summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryKb = 65536, Iterations = 3, Parallelism = 2, SaltBytes = 16, HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[0] != "argon2id") return false;
            var pairs = parts[2].Split(',').Select(kv => kv.Split('=')).ToList();
            if (pairs.Any(kv => kv.Length != 2)) return false;
            var p = pairs.ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));
            var salt = FromB64(parts[3]);
            var expected = FromB64(parts[4]);
            var actual = Compute(password, salt, p["m"], p["t"], p["p"], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, MemorySize = memoryKb, Iterations = iterations, DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');
    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}

public sealed class JwtAccessTokenService(IOptions<JwtOptions> options, IClock clock) : IAccessTokenService
{
    public AccessToken Issue(ApplicationUser user, IReadOnlyCollection<string> roles, bool isPlatformAdmin)
    {
        var o = options.Value;
        var expires = clock.UtcNow.AddMinutes(o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(MxClaims.OrganizationId, user.OrganizationId.ToString()),
        };
        if (isPlatformAdmin) claims.Add(new Claim(MxClaims.PlatformAdmin, "true"));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = clock.UtcNow.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(o.GetKey(), SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }
}

/// <summary>API keys: "mxk_{prefix}_{secret}". Only the SHA-256 of the whole key is stored.</summary>
public static class ApiKeyGenerator
{
    public static (string FullKey, string Prefix, string Hash) Create()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var full = $"mxk_{prefix}_{secret}";
        return (full, prefix, HashKey(full));
    }

    public static string HashKey(string fullKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullKey))).ToLowerInvariant();
}

public static class DependencyInjection
{
    public static IServiceCollection AddMxSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();
        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.Section));
        services.AddSingleton<IKeyProtector, AesGcmKeyProtector>();
        return services;
    }
}
