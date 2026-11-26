using System;
using System.Security.Cryptography;

namespace D6Downloader.Remote;

/// <summary>Ein offenes Pairing-Fenster: frischer Seed, 10 Minuten gültig, nimmt genau ein <c>hello</c> an.</summary>
public sealed class PairingSession
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private volatile bool _closed;

    public PairingSession(DateTimeOffset now, byte[]? seed = null)
    {
        Seed = seed ?? RandomNumberGenerator.GetBytes(RemoteCrypto.SeedSize);
        Keys = RemoteCrypto.Derive(Seed);
        Started = now;
        Expires = now + Window;
    }

    public byte[] Seed { get; }
    public RemoteKeys Keys { get; }
    public DateTimeOffset Started { get; }
    public DateTimeOffset Expires { get; }

    public string Code => RemoteCrypto.EncodeCode(Seed);

    public string Uri(string pcName, Uri server) => RemoteCrypto.PairUri(Seed, pcName, server);

    public bool IsOpen(DateTimeOffset now) => !_closed && now < Expires;

    public void Close() => _closed = true;
}
