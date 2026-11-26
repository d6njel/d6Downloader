using D6Downloader.Platform;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Hängt die Linux-Dienste ein, die statischer Code braucht, samt Schlüsseldatei (sie entsteht erst beim ersten Gebrauch).
/// Dienste mit Wirkung nach außen erzeugt die App selbst: <see cref="SystemdInhibitKeepAwake"/>, <see cref="LinuxPowerActions"/>,
/// <see cref="XdgAutostart"/>, <see cref="LinuxNativeHostLocations"/>, <see cref="XdgShell"/>, <see cref="IpcServer"/>.
/// </summary>
public static class LinuxPlatform
{
    public static void Register()
    {
        PlatformServices.Tools = new LinuxToolLocator();
        PlatformServices.Languages = new LinuxUserLanguages();
        PlatformServices.IoErrors = new LinuxIoErrorClassifier();
        PlatformServices.Trash = new GioTrash();
        PlatformServices.SecretKey = new KeyFileSecretKeyProvider();
    }
}
