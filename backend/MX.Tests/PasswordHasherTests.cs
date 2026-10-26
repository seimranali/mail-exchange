using MX.Security;

namespace MX.Tests;

public sealed class PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_verifies_correct_password_and_rejects_wrong_one()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.StartsWith("$argon2id$", hash);
        Assert.DoesNotContain("correct horse", hash);
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
        Assert.False(_hasher.Verify("Correct horse battery staple", hash));
    }

    [Fact]
    public void Same_password_produces_different_hashes()
    {
        Assert.NotEqual(_hasher.Hash("same"), _hasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$garbage$x$y")]
    public void Malformed_hash_does_not_verify(string hash) => Assert.False(_hasher.Verify("anything", hash));

    [Fact]
    public void Api_key_hash_is_deterministic_and_does_not_contain_the_key()
    {
        var (key, prefix, hash) = ApiKeyGenerator.Create();
        Assert.StartsWith($"mxk_{prefix}_", key);
        Assert.Equal(hash, ApiKeyGenerator.HashKey(key));
        Assert.DoesNotContain(key, hash);
    }
}
