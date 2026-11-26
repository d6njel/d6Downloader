using Microsoft.Win32.SafeHandles;
using D6Downloader.Platform;

namespace D6Downloader.Engine;

/// <summary>Dateisystem-Funktionen für die Engine: Sparse-Dateien und freier Platz.</summary>
internal static class NativeFile
{
    /// <summary>ENOSPC, so wie ihn .NET unter Linux in <c>HResult</c> ablegt.</summary>
    public const int ErrorDiskFull = 28;

    /// <summary>
    /// Linux-Dateisysteme legen beim Vergrößern oder beim Schreiben weit hinten von selbst Lücken an, ein Flag gibt es nicht.
    /// True heißt für den Aufrufer: Die Größe reserviert keinen Platz, der freie Platz ist vorher zu prüfen.
    /// </summary>
    public static bool TrySetSparse(SafeFileHandle handle, bool sparse) => true;

    /// <summary>Freier Platz für den aktuellen Benutzer, -1 wenn unbekannt.</summary>
    public static long FreeSpace(string directory) => PlatformServices.FreeSpace.Query(directory) ?? -1;
}
