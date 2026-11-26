using System;
using System.Security.Cryptography;
using D6Downloader.Debrid;
using D6Downloader.Platform;
using Xunit;

namespace D6Downloader.Tests;

public class SecretProtectorTests
{
    private sealed class Key : ISecretKeyProvider
    {
        private readonly byte[] _key;
        public Key(int length = 32) => _key = RandomNumberGenerator.GetBytes(length);
        public byte[] GetKey() => _key;
    }

    [Fact]
    public void Round_trip_hides_the_secret_and_uses_a_fresh_nonce()
    {
        var key = new Key();
        string first = SecretProtector.Protect("Geheim ÄÖ 123", key);
        string second = SecretProtector.Protect("Geheim ÄÖ 123", key);

        Assert.StartsWith("v1:", first);
        Assert.DoesNotContain("Geheim", first);
        Assert.NotEqual(first, second);
        Assert.Equal("Geheim ÄÖ 123", SecretProtector.Unprotect(first, key));
        Assert.Equal(12 + System.Text.Encoding.UTF8.GetByteCount("Geheim ÄÖ 123") + 16, Convert.FromBase64String(first[3..]).Length);
    }

    [Fact]
    public void Wrong_key_tampering_or_no_key_give_null()
    {
        var key = new Key();
        string text = SecretProtector.Protect("abc", key);
        byte[] data = Convert.FromBase64String(text[3..]);
        data[^1] ^= 1;

        Assert.Null(SecretProtector.Unprotect(text, new Key()));
        Assert.Null(SecretProtector.Unprotect("v1:" + Convert.ToBase64String(data), key));
        Assert.Null(SecretProtector.Unprotect(text, null));
        Assert.Null(SecretProtector.Unprotect(text, new Key(16)));
    }

    [Fact]
    public void Protect_never_falls_back_to_a_weak_or_missing_key() =>
        Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Protect("abc", new Key(16)));
}
