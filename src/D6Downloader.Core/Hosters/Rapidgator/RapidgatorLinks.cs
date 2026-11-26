using System;
using System.Linq;

namespace D6Downloader.Hosters.Rapidgator;

/// <param name="FileId">Kennung der Datei bzw. des Ordners.</param>
/// <param name="NameHint">Dateiname aus der Adresse (ohne angehängtes <c>.html</c>), null = keiner.</param>
public sealed record RapidgatorLink(string FileId, string? NameHint, bool IsFolder);

/// <summary>
/// Erkennt Rapidgator-Links: <c>/file/&lt;id&gt;[/&lt;name&gt;[.html]]</c>, Ordner <c>/folder/&lt;id&gt;…</c>. Die Doku zeigt IDs aus
/// 32 Hex-Zeichen; reine Zahlen (ältere Links) werden ebenfalls angenommen. <c>rg.to</c> ist nicht dokumentiert, aber als
/// Kurzdomain üblich (Annahme).
/// </summary>
public static class RapidgatorLinks
{
    public static readonly string[] Domains = { "rapidgator.net", "rg.to" };

    public static RapidgatorLink? Parse(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Parse(uri) : null;

    public static RapidgatorLink? Parse(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return null;
        string host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (!Domains.Contains(host)) return null;

        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        bool folder = parts[0].Equals("folder", StringComparison.OrdinalIgnoreCase);
        if (!folder && !parts[0].Equals("file", StringComparison.OrdinalIgnoreCase)) return null;
        if (!IsId(parts[1])) return null;
        if (folder) return new RapidgatorLink(parts[1], null, true);
        if (parts.Length > 3) return null;

        string? name = null;
        if (parts.Length == 3)
        {
            name = Uri.UnescapeDataString(parts[2]);
            if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            if (name.Length == 0) name = null;
        }
        return new RapidgatorLink(parts[1], name, false);
    }

    public static bool IsRapidgator(string? url) => Parse(url) is not null;

    private static bool IsId(string id) =>
        (id.Length == 32 && id.All(char.IsAsciiHexDigit)) || (id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit));
}
