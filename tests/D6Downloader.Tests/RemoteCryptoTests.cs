using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using D6Downloader.Remote;
using Xunit;

namespace D6Downloader.Tests;

public class RemoteCryptoTests
{
    // ---- Interop-Vektoren: dieselben Konstanten stehen in der Android-App (RemoteCryptoTest.kt). ----
    internal static readonly byte[] Seed = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(0xa0, 12).Select(i => (byte)i).ToArray();
    private const string VectorPlain = "{\"t\":\"ping\",\"id\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"ts\":1700000000000}";
    private const string VectorEnvelope = "d6r1.oKGio6SlpqeoqaqrlF84NjhXlVYR1utCMbmHcdbwbJE8Rk_yvNjPisTJ2sA3qjPTk6Fi0TOcsEyGutR1zQqB9eSIUq2nYRrnWSBfyp47xmgeXgf4JVyVoXQ";

    [Fact]
    public void Hkdf_matches_rfc_5869_test_case_1()
    {
        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, Enumerable.Repeat((byte)0x0b, 22).ToArray(), 42,
            Convert.FromHexString("000102030405060708090a0b0c"), Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9"));
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexStringLower(okm));
    }

    [Fact]
    public void Derivation_matches_the_interop_vectors()
    {
        var keys = RemoteCrypto.Derive(Seed);
        Assert.Equal("2591f0e5abc098d86764f9d5ec29a6be153852292a26073bda9d5ab5ef21e54c", Convert.ToHexStringLower(keys.KeyUp));
        Assert.Equal("20175b727033dc02bdc68477be04b11e548c1ba211e9eeff85b60c807eb3c3d6", Convert.ToHexStringLower(keys.KeyDown));
        Assert.Equal("d6rvh58z0ganpsm0swhvvtjv7s5", keys.TopicUp);
        Assert.Equal("d6rnys4vd47tfz605wyy5zk0vfx", keys.TopicDown);
        Assert.Equal("jx837f4p", keys.DeviceId);
        Assert.Equal("000G-40R4-0M30-E209-185G-R38E-1W", RemoteCrypto.EncodeCode(Seed));
    }

    [Fact]
    public void Envelope_matches_the_interop_vector()
    {
        var keys = RemoteCrypto.Derive(Seed);
        string sealedText = RemoteCrypto.Seal(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, Encoding.UTF8.GetBytes(VectorPlain), Nonce);
        Assert.Equal(VectorEnvelope, sealedText);
        Assert.Equal(VectorPlain, Encoding.UTF8.GetString(RemoteCrypto.Open(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, sealedText)!));
    }

    [Fact]
    public void Tampering_wrong_direction_topic_or_device_is_rejected()
    {
        var keys = RemoteCrypto.Derive(Seed);
        byte[] plain = Encoding.UTF8.GetBytes("geheim");
        string body = RemoteCrypto.Seal(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, plain);
        byte[]? Open(byte[] key, string dir, string device, string topic, string text) =>
            RemoteCrypto.Open(key, dir, device, topic, text);

        Assert.NotNull(Open(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, body));
        Assert.Null(Open(keys.KeyDown, "up", keys.DeviceId, keys.TopicUp, body));
        Assert.Null(Open(keys.KeyUp, "down", keys.DeviceId, keys.TopicUp, body));
        Assert.Null(Open(keys.KeyUp, "up", "andere00", keys.TopicUp, body));
        Assert.Null(Open(keys.KeyUp, "up", keys.DeviceId, keys.TopicDown, body));
        for (int i = 5; i < body.Length - 1; i++) // jedes Zeichen außer dem letzten (Füllbits)
        {
            string changed = body[..i] + (body[i] == 'A' ? 'B' : 'A') + body[(i + 1)..];
            Assert.Null(Open(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, changed));
        }
        foreach (string bad in new[] { "", "d6r1.", "d6r1.!!!!", "d6r2." + body[5..], "hallo", "d6r1.AAAA", "d6r1." + new string('A', 4000) })
            Assert.Null(Open(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, bad));
    }

    [Fact]
    public void Largest_plaintext_fits_the_envelope_limit()
    {
        var keys = RemoteCrypto.Derive(Seed);
        string body = RemoteCrypto.Seal(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, new byte[RemoteCrypto.MaxPlain]);
        Assert.True(body.Length <= RemoteCrypto.MaxEnvelope);
        Assert.Throws<ArgumentException>(() =>
            RemoteCrypto.Seal(keys.KeyUp, "up", keys.DeviceId, keys.TopicUp, new byte[RemoteCrypto.MaxPlain + 1]));
    }

    [Fact]
    public void Code_is_read_back_and_tolerates_typical_typing()
    {
        for (int i = 0; i < 100; i++)
        {
            byte[] seed = RandomNumberGenerator.GetBytes(16);
            string code = RemoteCrypto.EncodeCode(seed);
            Assert.Equal(26, code.Replace("-", "").Length);
            Assert.Equal(seed, RemoteCrypto.DecodeCode(code));
            Assert.Equal(seed, RemoteCrypto.DecodeCode(" " + code.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l')));
        }
        Assert.Equal(Seed, RemoteCrypto.DecodeCode("OOOG40R4OM3OE2O9I85GR38EIW"));
        foreach (string? bad in new[] { null, "", "000G", "000G40R40M30E209185GR38E1WW", "U00G40R40M30E209185GR38E1W", "000G40R40M30E209185GR38E1Z" })
            Assert.Null(RemoteCrypto.DecodeCode(bad));
    }

    [Fact]
    public void Pair_uri_names_the_server_only_if_it_is_not_ntfy()
    {
        Assert.Equal("d6remote://pair?v=1&k=000G40R40M30E209185GR38E1W&n=mein%20pc",
            RemoteCrypto.PairUri(Seed, "mein pc", RemoteSettings.ParseServer("https://ntfy.sh")!));
        Assert.EndsWith("&s=https%3A%2F%2Fntfy.example.org%3A8443",
            RemoteCrypto.PairUri(Seed, "pc", RemoteSettings.ParseServer("https://ntfy.example.org:8443")!));
    }

    [Theory]
    [InlineData("http://ntfy.sh")]
    [InlineData("ntfy.sh")]
    [InlineData("https://nutzer:pw@ntfy.sh")]
    [InlineData("https://ntfy.sh/?x=1")]
    [InlineData("ftp://ntfy.sh")]
    [InlineData("")]
    public void Only_plain_https_servers_are_accepted(string url)
    {
        Assert.Null(RemoteSettings.ParseServer(url));
    }
}
