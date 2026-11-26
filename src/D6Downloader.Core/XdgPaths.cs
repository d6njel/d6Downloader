using System;
using System.IO;

namespace D6Downloader;

/// <summary>
/// Ordner nach der XDG-Spezifikation. Die Umgebung ist austauschbar, damit Tests nie die echten Ordner des Benutzers
/// anfassen. Relative Werte gelten nach der Spezifikation als nicht gesetzt.
/// </summary>
public sealed class XdgPaths
{
    private readonly Func<string, string?> _environment;

    public XdgPaths(Func<string, string?>? environment = null) =>
        _environment = environment ?? Environment.GetEnvironmentVariable;

    public string Home => Absolute("HOME") ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolderOption.DoNotVerify)) ?? throw new IOException("HOME ist nicht gesetzt.");

    /// <summary><c>$XDG_CONFIG_HOME</c>, sonst <c>~/.config</c>, auch wenn es den Ordner noch nicht gibt.</summary>
    public string ConfigHome => Absolute("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");

    /// <summary><c>~/.config/d6downloader</c>.</summary>
    public string AppConfigDir => Path.Combine(ConfigHome, "d6downloader");

    /// <summary><c>$XDG_RUNTIME_DIR</c>, null wenn nicht gesetzt.</summary>
    public string? RuntimeDir => Absolute("XDG_RUNTIME_DIR");

    /// <summary><c>XDG_DOWNLOAD_DIR</c> aus <c>user-dirs.dirs</c>, sonst <c>~/Downloads</c>.</summary>
    public string DownloadDir
    {
        get
        {
            string home = Home;
            string? found = null;
            try
            {
                string file = Path.Combine(ConfigHome, "user-dirs.dirs");
                if (File.Exists(file))
                    foreach (string line in File.ReadLines(file))
                        found = ParseUserDir(line, "XDG_DOWNLOAD_DIR", home) ?? found; // wie in der Shell gilt die letzte Zeile
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            // Der Benutzerordner selbst bedeutet bei xdg-user-dirs "abgeschaltet".
            return found is not null && found.TrimEnd('/') != home.TrimEnd('/') ? found : Path.Combine(home, "Downloads");
        }
    }

    /// <summary>Eine Zeile <c>NAME="$HOME/Ordner"</c> oder <c>NAME="/absolut"</c>. Null für alles andere.</summary>
    internal static string? ParseUserDir(string line, string name, string home)
    {
        string text = line.Trim();
        if (!text.StartsWith(name + "=\"", StringComparison.Ordinal)) return null;
        var value = new System.Text.StringBuilder();
        for (int i = name.Length + 2; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                string result = value.ToString();
                if (result == "$HOME") return home;
                if (result.StartsWith("$HOME/", StringComparison.Ordinal)) return Path.Combine(home, result[6..].TrimStart('/'));
                return result.StartsWith('/') ? result : null;
            }
            if (c == '\\' && i + 1 < text.Length) c = text[++i];
            value.Append(c);
        }
        return null; // Anführungszeichen fehlt
    }

    private string? Absolute(string variable) =>
        _environment(variable) is { Length: > 0 } value && value.StartsWith('/') ? value : null;

    private static string? NonEmpty(string value) => value.Length > 0 && value.StartsWith('/') ? value : null;
}
