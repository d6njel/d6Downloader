using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace D6Downloader.Remote;

/// <summary>Alles, was beide Seiten aus dem Seed ableiten.</summary>
public sealed record RemoteKeys(byte[] KeyUp, byte[] KeyDown, string TopicUp, string TopicDown, string DeviceId);

/// <summary>
/// Protokoll v1: HKDF-Ableitungen, Base32 und der Umschlag <c>d6r1.</c> + Base64url(Nonce | Chiffre | Prüfsumme) mit
/// AES-256-GCM. Richtung, Gerät und Topic stehen in der AAD: Eine Nachricht lässt sich weder zurückspiegeln noch umleiten.
/// Dieselben Regeln stehen in der Android-App (gemeinsame Testvektoren).
/// </summary>
public static class RemoteCrypto
{
    public const string Up = "up", Down = "down";
    public const int SeedSize = 16, MaxEnvelope = 4000, MaxPlain = 2800, CodeLength = 26;
    private const string Context = "d6remote/1";
    private const string Prefix = "d6r1.";
    private const int KeySize = 32, NonceSize = 12, TagSize = 16;

    // Crockford: ohne I, L, O, U.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static RemoteKeys Derive(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize) throw new ArgumentException("Der Seed hat nicht 16 Byte.", nameof(seed));
        byte[] ikm = seed.ToArray();
        byte[] Hkdf(string info, int length) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length,
            Encoding.ASCII.GetBytes(Context), Encoding.ASCII.GetBytes(info));
        return new RemoteKeys(
            Hkdf("key up", KeySize),
            Hkdf("key down", KeySize),
            "d6r" + Base32Lower(Hkdf("topic up", 15)),
            "d6r" + Base32Lower(Hkdf("topic down", 15)),
            Base32Lower(Hkdf("device id", 5)));
    }

    public static string Seal(byte[] key, string direction, string deviceId, string topic, ReadOnlySpan<byte> plain) =>
        Seal(key, direction, deviceId, topic, plain, RandomNumberGenerator.GetBytes(NonceSize));

    /// <summary>Mit fester Nonce, nur für Testvektoren: Eine wiederholte Nonce bricht GCM.</summary>
    internal static string Seal(byte[] key, string direction, string deviceId, string topic, ReadOnlySpan<byte> plain, byte[] nonce)
    {
        if (plain.Length > MaxPlain) throw new ArgumentException("Nachricht zu groß.", nameof(plain));
        var data = new byte[NonceSize + plain.Length + TagSize];
        nonce.CopyTo(data, 0);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, data.AsSpan(NonceSize, plain.Length), data.AsSpan(NonceSize + plain.Length),
            Aad(direction, deviceId, topic));
        return Prefix + Base64Url.EncodeToString(data);
    }

    /// <summary>Null, wenn der Umschlag nicht passt (fremd, verändert, falsche Richtung, zu groß). Nie eine Ausnahme.</summary>
    public static byte[]? Open(byte[] key, string direction, string deviceId, string topic, string? body)
    {
        if (body is null || body.Length > MaxEnvelope || !body.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            byte[] data = Base64Url.DecodeFromChars(body.AsSpan(Prefix.Length));
            int length = data.Length - NonceSize - TagSize;
            if (length < 0) return null;
            var plain = new byte[length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, length), data.AsSpan(NonceSize + length), plain,
                Aad(direction, deviceId, topic));
            return plain;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Aad(string direction, string deviceId, string topic) =>
        Encoding.UTF8.GetBytes($"{Context}|{direction}|{deviceId}|{topic}");

    /// <summary>Zufällige Kennung (16 Byte, Base64url).</summary>
    public static string NewId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static string Base32(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = ((buffer << 8) | b) & 0xFFF;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    internal static string Base32Lower(ReadOnlySpan<byte> data) => Base32(data).ToLowerInvariant();

    /// <summary>Der Seed zum Abtippen, 26 Zeichen in Gruppen zu vier.</summary>
    public static string EncodeCode(ReadOnlySpan<byte> seed)
    {
        string raw = Base32(seed);
        var text = new StringBuilder(raw.Length + 6);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 4 == 0) text.Append('-');
            text.Append(raw[i]);
        }
        return text.ToString();
    }

    /// <summary>Liest einen eingetippten Code: Bindestriche und Leerzeichen egal, O gilt als 0, I und L als 1.</summary>
    public static byte[]? DecodeCode(string? code)
    {
        if (code is null) return null;
        var seed = new byte[SeedSize];
        int buffer = 0, bits = 0, count = 0, written = 0;
        foreach (char raw in code)
        {
            if (raw == '-' || char.IsWhiteSpace(raw)) continue;
            char c = char.ToUpperInvariant(raw);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            int value = Alphabet.IndexOf(c);
            if (value < 0 || ++count > CodeLength) return null;
            buffer = ((buffer << 5) | value) & 0xFFF;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                seed[written++] = (byte)(buffer >> bits);
            }
        }
        // Die zwei Füllbits am Ende müssen null sein.
        return count == CodeLength && (buffer & ((1 << bits) - 1)) == 0 ? seed : null;
    }

    /// <summary>Der Inhalt des QR-Codes. Der Server steht nur darin, wenn es nicht ntfy.sh ist.</summary>
    public static string PairUri(ReadOnlySpan<byte> seed, string pcName, Uri server)
    {
        string uri = "d6remote://pair?v=1&k=" + Base32(seed) + "&n=" + Uri.EscapeDataString(pcName);
        if (!RemoteSettings.IsDefaultServer(server)) uri += "&s=" + Uri.EscapeDataString(server.AbsoluteUri.TrimEnd('/'));
        return uri;
    }
}
