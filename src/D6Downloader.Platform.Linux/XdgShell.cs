using System;
using System.IO;
using System.Linq;

namespace D6Downloader.Platform.Linux;

/// <summary><c>xdg-open</c> zum Öffnen, <c>org.freedesktop.FileManager1.ShowItems</c> zum Zeigen im Ordner.</summary>
public sealed class XdgShell : IShell
{
    private readonly ICommandRunner _commands;

    public XdgShell(ICommandRunner? commands = null) => _commands = commands ?? CommandRunner.Instance;

    /// <summary>Nur absolute Pfade und http(s)-Adressen. Wirft IOException, wenn sich nichts starten lässt.</summary>
    public void Open(string pathOrUrl)
    {
        bool web = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
        if (!web && !pathOrUrl.StartsWith('/')) throw new IOException("Weder absoluter Pfad noch Webadresse: " + pathOrUrl);
        _commands.Start(_commands.Find("xdg-open") ?? throw new IOException("xdg-open fehlt."), new[] { pathOrUrl });
    }

    /// <summary>Markiert die Datei im Dateimanager. Geht das nicht, wird der Ordner darüber geöffnet.</summary>
    public void ShowInFolder(string path)
    {
        string full = Path.GetFullPath(path);
        if (_commands.Find("busctl") is { } busctl)
        {
            try
            {
                var result = _commands.Run(busctl, new[]
                {
                    "--user", "call", "org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1", "ShowItems", "ass", "1", FileUri(full), "",
                }, TimeSpan.FromSeconds(5));
                if (result.ExitCode is null or 0) return;
            }
            catch (IOException)
            {
            }
        }
        Open(Path.GetDirectoryName(full) ?? "/");
    }

    /// <summary>Jeder Pfadteil einzeln kodiert, damit <c>#</c>, <c>?</c> und <c>%</c> im Namen bleiben.</summary>
    internal static string FileUri(string path) => "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
