using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using D6Downloader.Engine;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Hauptschlüssel aus <c>~/.config/d6downloader/master.key</c> (32 Byte, 0600, Ordner 0700). Wird beim ersten Gebrauch
/// erzeugt. Eine vorhandene, aber unbrauchbare Datei wird nie überschrieben: Damit wären alle Geheimnisse verloren.
/// Auch im Portable-Modus bleibt der Schlüssel beim Benutzerkonto und wandert nicht mit dem Programmordner.
/// </summary>
public sealed class KeyFileSecretKeyProvider : ISecretKeyProvider
{
    private const int KeySize = 32;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Func<string> _path;
    private readonly object _lock = new();
    private byte[]? _key;

    public KeyFileSecretKeyProvider(string? path = null) =>
        _path = path is null ? () => Path.Combine(new XdgPaths().AppConfigDir, "master.key") : () => path;

    public byte[] GetKey()
    {
        lock (_lock)
        {
            _key ??= Load(_path());
            return (byte[])_key.Clone();
        }
    }

    private static byte[] Load(string path)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) CreateLocked(path);
        if (File.GetUnixFileMode(path) != OwnerOnly) File.SetUnixFileMode(path, OwnerOnly);
        using var stream = File.OpenRead(path);
        if (stream.Length != KeySize) throw new IOException($"Schlüsseldatei beschädigt (nicht {KeySize} Byte): {path}");
        var key = new byte[KeySize];
        stream.ReadExactly(key);
        return key;
    }

    /// <summary>
    /// Unter einer Sperrdatei, damit nur einer einen Schlüssel erzeugt: <c>File.Move</c> ohne Überschreiben ist unter Linux
    /// nicht atomar, zwei Prozesse könnten sonst verschiedene Schlüssel benutzen.
    /// </summary>
    private static void CreateLocked(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, UnixCreateMode = OwnerOnly,
        };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var held = new FileStream(path + ".lock", options);
                if (!File.Exists(path)) Create(path);
                return;
            }
            catch (IOException ex) when (ex.HResult == 11 && attempt < 500) // EWOULDBLOCK: ein anderer erzeugt gerade
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>Erst vollständig in eine Nebendatei, dann an den Platz: nie eine halbe Schlüsseldatei.</summary>
    private static void Create(string path)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerOnly };
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(RandomNumberGenerator.GetBytes(KeySize));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
