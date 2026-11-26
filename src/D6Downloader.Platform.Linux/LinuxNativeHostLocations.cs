using System.Collections.Generic;
using System.IO;
using System.Linq;
using D6Downloader.Browser;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Die Manifest-Ordner der Browser, nur wenn es den Profilordner schon gibt. Flatpak- und Snap-Browser sehen diese Ordner nicht.
/// </summary>
public sealed class LinuxNativeHostLocations : INativeHostLocations
{
    private static readonly string[] ChromeProfiles =
        { "google-chrome", "chromium", "BraveSoftware/Brave-Browser", "vivaldi", "microsoft-edge" };

    private readonly XdgPaths _xdg;

    public LinuxNativeHostLocations(XdgPaths? xdg = null) => _xdg = xdg ?? new XdgPaths();

    public IReadOnlyList<string> ManifestDirectories(BrowserKind kind)
    {
        if (kind == BrowserKind.Chrome)
            return ChromeProfiles.Select(p => Path.Combine(_xdg.ConfigHome, p))
                .Where(Directory.Exists)
                .Select(p => Path.Combine(p, "NativeMessagingHosts"))
                .ToList();

        string mozilla = Path.Combine(_xdg.Home, ".mozilla");
        // Neue Firefox-Profile können unter ~/.config/mozilla liegen; die Hosts werden weiter in ~/.mozilla gesucht.
        return Directory.Exists(mozilla) || Directory.Exists(Path.Combine(_xdg.ConfigHome, "mozilla", "firefox"))
            ? new[] { Path.Combine(mozilla, "native-messaging-hosts") }
            : System.Array.Empty<string>();
    }
}
