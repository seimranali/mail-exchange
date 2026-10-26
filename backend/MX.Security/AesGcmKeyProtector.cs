using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MX.Application.Abstractions;

namespace MX.Security;

public sealed class SecretsOptions
{
    public const string Section = "Secrets";

    /// <summary>
    /// Base64 256-bit key-encryption key. Supply from an environment variable or secret store
    /// (Secrets__KeyEncryptionKey); never commit a production value.
    /// </summary>
    public string KeyEncryptionKey { get; set; } = string.Empty;
}

/// <summary>
/// AES-256-GCM envelope for secrets at rest. Output: base64(nonce[12] | tag[16] | ciphertext).
/// The purpose string is bound as associated data, so a ciphertext cannot be moved to another record.
/// </summary>
public sealed class AesGcmKeyProtector : IKeyProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmKeyProtector(IOptions<SecretsOptions> options)
    {
        try
        {
            _key = Convert.FromBase64String(options.Value.KeyEncryptionKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Secrets:KeyEncryptionKey must be base64.");
        }
        if (_key.Length != 32) throw new InvalidOperationException("Secrets:KeyEncryptionKey must be 32 bytes (base64).");
    }

    public string Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(purpose));
        return Convert.ToBase64String(output);
    }

    public byte[] Unprotect(string protectedValue, string purpose)
    {
        var input = Convert.FromBase64String(protectedValue);
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("Protected value is too short.");
        var plaintext = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }
}
