using System;
using System.IO;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Nur eine laufende App: eine Datei mit <see cref="FileShare.None"/> (unter Linux eine exklusive <c>flock</c>-Sperre). Der Kern
/// gibt sie auch nach einem Absturz frei, die Datei selbst bleibt liegen.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream, RuntimePaths paths)
    {
        _stream = stream;
        Paths = paths;
    }

    internal RuntimePaths Paths { get; }

    /// <summary>Null, wenn schon eine Instanz läuft. Wirft IOException oder UnauthorizedAccessException bei anderen Fehlern.</summary>
    public static SingleInstanceLock? TryAcquire(RuntimePaths paths)
    {
        paths.Prepare();
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            return new SingleInstanceLock(new FileStream(paths.LockFile, options), paths);
        }
        catch (IOException ex) when (ex.HResult == 11) // EWOULDBLOCK: die Sperre ist vergeben
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
