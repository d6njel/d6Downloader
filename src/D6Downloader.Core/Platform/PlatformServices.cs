using System;
using System.IO;

namespace D6Downloader.Platform;

/// <summary>Freier Platz über <see cref="DriveInfo"/>, das unter Linux jeden vorhandenen Ordner annimmt.</summary>
public sealed class DriveInfoFreeSpace : IFreeSpace
{
    public static readonly DriveInfoFreeSpace Instance = new();

    public long? Query(string directory)
    {
        try
        {
            if (string.IsNullOrEmpty(directory) || directory.Contains('\0')) return null;
            for (string? dir = Path.GetFullPath(directory); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(dir))
                    return new DriveInfo(dir).AvailableFreeSpace;
            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>
/// Die Plattformdienste, die auch aus statischem Code gebraucht werden. Das Programm (und die Tests) hängen sie einmal beim
/// Start ein, vor der ersten Benutzung. Dienste mit Wirkung nach außen (Wachhalten, Ausschalten, Timer) werden dagegen dem
/// <c>DownloadManager</c> übergeben.
/// </summary>
public static class PlatformServices
{
    public static IFreeSpace FreeSpace { get; set; } = DriveInfoFreeSpace.Instance;
    public static IToolLocator Tools { get; set; } = NoToolLocator.Instance;
    public static IUserLanguages Languages { get; set; } = NoUserLanguages.Instance;
    public static ITrash Trash { get; set; } = NoTrash.Instance;
    public static IIoErrorClassifier IoErrors { get; set; } = NoIoErrorClassifier.Instance;

    /// <summary>Null, solange keiner eingehängt ist: Dann lässt sich nichts verschlüsseln und nichts lesen.</summary>
    public static ISecretKeyProvider? SecretKey { get; set; }
}
