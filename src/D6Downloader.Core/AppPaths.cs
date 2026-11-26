using System;
using System.IO;

namespace D6Downloader;

/// <summary>Speicherorte. Liegt eine Datei <c>.portable</c> neben der exe, bleiben die Daten im Programmordner.</summary>
public static class AppPaths
{
    private static string? _dataDir;

    private static string MarkerPath => Path.Combine(AppContext.BaseDirectory, ".portable");

    public static bool IsPortable => File.Exists(MarkerPath);

    public static string DataDir => _dataDir ??= CreateDataDir();

    /// <summary>
    /// Schaltet den Portable-Modus um: Marker anlegen oder löschen, ab dann gilt der andere Datenordner. Die Dateien dorthin
    /// zu schreiben ist Sache des Aufrufers. Wirft, wenn der Programmordner nicht beschreibbar ist.
    /// </summary>
    public static void SetPortable(bool portable)
    {
        if (portable) File.WriteAllBytes(MarkerPath, Array.Empty<byte>());
        else File.Delete(MarkerPath);
        _dataDir = null;
    }

    public static string DownloadsFile => Path.Combine(DataDir, "downloads.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    private static string CreateDataDir()
    {
        if (!IsPortable)
        {
            // Nie relativ, auch wenn es ~/.config noch nicht gibt. Nur für den Benutzer, wegen der Schlüsseldatei darin.
            string own = new XdgPaths().AppConfigDir;
            Engine.PrivateDirectory.Ensure(own);
            return own;
        }
        string dir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Downloads-Ordner des Benutzers laut <c>user-dirs.dirs</c>.</summary>
    public static string UserDownloads() => new XdgPaths().DownloadDir;
}
