using System;
using System.IO;
using D6Downloader.Browser;

namespace D6Downloader.Platform.Linux;

/// <summary>Verbindung des Native-Messaging-Hosts zur laufenden App über den Socket.</summary>
public sealed class UnixSocketAppLink : IAppLink
{
    private readonly RuntimePaths _paths;
    private readonly string? _appPath;
    private readonly ICommandRunner _commands;

    /// <param name="appPath">Programmdatei der App, die <see cref="Launch"/> startet.</param>
    public UnixSocketAppLink(RuntimePaths? paths = null, string? appPath = null, ICommandRunner? commands = null)
    {
        _paths = paths ?? RuntimePaths.Resolve();
        _appPath = appPath ?? Environment.ProcessPath;
        _commands = commands ?? CommandRunner.Instance;
    }

    public bool TrySend(string text) => IpcClient.TrySendLinks(_paths, text);

    /// <summary>
    /// Startet die App in einer eigenen Sitzung (der Browser beendet beim Schließen die Prozessgruppe des Hosts) und ohne die
    /// Datenströme des Browsers: Auf stdout läuft das Native-Messaging-Protokoll.
    /// </summary>
    public bool Launch()
    {
        if (_appPath is null || _commands.Find("setsid") is not { } setsid) return false;
        try
        {
            // Die Shell dient nur der Umleitung. Die Pfade sind Argumente und werden nie als Befehl gelesen.
            _commands.Start("/bin/sh", new[] { "-c", "exec \"$1\" -f \"$2\" </dev/null >/dev/null 2>&1", "sh", setsid, _appPath });
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
