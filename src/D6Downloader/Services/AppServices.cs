using System;
using System.IO;
using D6Downloader.Browser;
using D6Downloader.Platform;
using D6Downloader.Platform.Linux;

namespace D6Downloader.Services;

/// <summary>
/// Alles, womit die Oberfläche nach außen wirkt. Die App füllt es mit den Linux-Diensten, Tests mit Attrappen: So öffnet kein
/// Test den Dateimanager, schreibt einen Autostart-Eintrag oder registriert etwas im Browser.
/// </summary>
public sealed record AppServices(IShell Shell, IAutostart Autostart, BrowserSetup Browser, INotifier Notifier, string ExtensionDir)
{
    public static AppServices CreateLinux() => new(
        new XdgShell(),
        new XdgAutostart(),
        new BrowserSetup(new LinuxNativeHostLocations(), Environment.ProcessPath!),
        new DbusNotifier(),
        Path.Combine(AppContext.BaseDirectory, "Extension"));
}
