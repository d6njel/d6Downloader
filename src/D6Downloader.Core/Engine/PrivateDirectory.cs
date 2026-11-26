using System;
using System.IO;

namespace D6Downloader.Engine;

/// <summary>Ordner, den nur der Benutzer selbst lesen darf (0700).</summary>
internal static class PrivateDirectory
{
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        const UnixFileMode own = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, own);
        if (File.GetUnixFileMode(path) != own) File.SetUnixFileMode(path, own);
    }
}
