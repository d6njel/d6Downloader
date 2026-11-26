using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using D6Downloader.Platform;

namespace D6Downloader.Debrid;

/// <summary>
/// Schützt Geheimnisse (API-Key, Archiv-Passwörter) mit AES-256-GCM: In <c>settings.json</c> steht nur Chiffre, kein Klartext.
/// Format <c>v1:</c> + Base64(Nonce | Chiffre | Prüfsumme). Der Hauptschlüssel kommt von <see cref="ISecretKeyProvider"/>.
/// Ohne diesen Schlüssel (anderer Benutzer, anderer Rechner) ist der Text nicht lesbar.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "v1:";
    private const int KeySize = 32, NonceSize = 12, TagSize = 16;

    /// <summary>Verschlüsselt. Wirft, wenn kein Schlüssel zu bekommen ist: Klartext wird nie als Ersatz geliefert.</summary>
    public static string Protect(string secret) => Protect(secret,
        PlatformServices.SecretKey ?? throw new InvalidOperationException("Kein Hauptschlüssel eingerichtet."));

    internal static string Protect(string secret, ISecretKeyProvider provider)
    {
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var data = new byte[NonceSize + plain.Length + TagSize];
            var nonce = data.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(Key(provider), TagSize);
            aes.Encrypt(nonce, plain, data.AsSpan(NonceSize, plain.Length), data.AsSpan(NonceSize + plain.Length));
            return Prefix + Convert.ToBase64String(data);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>Entschlüsselt. Null, wenn es nicht geht (anderer oder fehlender Schlüssel, beschädigt, fremdes Format).</summary>
    public static string? Unprotect(string? protectedText) => Unprotect(protectedText, PlatformServices.SecretKey);

    internal static string? Unprotect(string? protectedText, ISecretKeyProvider? provider)
    {
        if (provider is null) return null;
        if (protectedText is null || !protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        byte[]? plain = null;
        try
        {
            byte[] data = Convert.FromBase64String(protectedText[Prefix.Length..]);
            int length = data.Length - NonceSize - TagSize;
            if (length < 0) return null;
            plain = new byte[length];
            using var aes = new AesGcm(Key(provider), TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, length), data.AsSpan(NonceSize + length), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (plain is not null) Array.Clear(plain);
        }
    }

    private static byte[] Key(ISecretKeyProvider provider)
    {
        byte[] key = provider.GetKey();
        return key.Length == KeySize ? key : throw new CryptographicException("Der Hauptschlüssel hat nicht 32 Byte.");
    }
}
