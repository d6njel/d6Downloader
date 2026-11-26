using System;
using System.IO;
using System.Linq;
using System.Text;
using D6Downloader.Engine;

namespace D6Downloader.Platform.Linux;

/// <summary>Autostart über <c>~/.config/autostart/d6downloader.desktop</c>. Die App startet damit still im Tray.</summary>
public sealed class XdgAutostart : IAutostart
{
    public const string Argument = "--autostart";
    public const string FileName = "d6downloader.desktop";

    private readonly string _file;
    private readonly string? _appPath;

    public XdgAutostart(XdgPaths? xdg = null, string? appPath = null)
    {
        _file = Path.Combine((xdg ?? new XdgPaths()).ConfigHome, "autostart", FileName);
        _appPath = appPath ?? Environment.ProcessPath;
    }

    public bool IsEnabled()
    {
        try
        {
            // Die Arbeitsumgebung schaltet einen Eintrag mit "Hidden=true" ab.
            return File.Exists(_file) && !File.ReadLines(_file).Any(l => l.Replace(" ", "") == "Hidden=true");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Schreibt den Eintrag mit dem aktuellen Pfad (korrigiert auch ein verschobenes Programm) oder löscht ihn.</summary>
    public void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(_file)) File.Delete(_file);
            return;
        }
        if (_appPath is null || _appPath.Any(char.IsControl)) throw new IOException(Loc.T("Autostart.PathUnknown"));
        string entry = "[Desktop Entry]\nType=Application\nName=d6Downloader\n" +
                       $"Exec={Quote(_appPath)} {Argument}\nIcon=d6downloader\nTerminal=false\n";
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        AtomicFile.WriteAllBytes(_file, new UTF8Encoding(false).GetBytes(entry));
    }

    /// <summary>
    /// Ein Argument für <c>Exec</c> nach der Desktop-Entry-Spezifikation: erst die Regeln für Anführungszeichen, dann die für
    /// Zeichenketten (jeder Backslash doppelt). <c>%</c> leitet Platzhalter ein und wird verdoppelt.
    /// </summary>
    internal static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in argument)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
            if (c == '%') quoted.Append('%');
        }
        return quoted.Append('"').ToString().Replace("\\", "\\\\");
    }
}
