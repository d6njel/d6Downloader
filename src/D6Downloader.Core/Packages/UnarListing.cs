using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace D6Downloader.Packages;

/// <param name="Name">Pfad im Archiv, null wenn keiner genannt ist.</param>
/// <param name="Size">Entpackte Größe, -1 wenn unbekannt.</param>
/// <param name="IsLink">Symbolische oder harte Verknüpfung, Umleitung oder Sonderdatei (Gerät, FIFO).</param>
internal sealed record UnarEntry(string? Name, long Size, bool IsDirectory, bool IsLink, bool Encrypted, bool Corrupted, bool ResourceFork);

/// <summary>Das Verzeichnis aus <c>lsar -j</c> (Format 2, Feldnamen wie bei unar 1.10.7).</summary>
/// <param name="Error">Fehlernummer von XADMaster (<c>lsarError</c>), null ohne Fehler.</param>
internal sealed record UnarListing(IReadOnlyList<UnarEntry> Entries, int? Error, bool Encrypted, bool Corrupted, IReadOnlyList<string> Volumes)
{
    /// <summary>Zeigen die Namen der gefundenen Teile eine Lücke? <c>lsar</c> meldet das nicht in jedem Fall selbst.</summary>
    public bool HasVolumeGap => PackageDetector.Group(Volumes, v => Path.GetFileName(v)).Any(g => g.Missing.Count > 0);

    // Fehlernummern von XADMaster, soweit sie unterschieden werden.
    public const int NotSupportedError = 7, PasswordError = 15;

    /// <summary>Null, wenn der Text kein vollständiges Verzeichnis ist.</summary>
    public static UnarListing? Parse(string json)
    {
        try
        {
            int start = json.IndexOf('{');
            if (start < 0) return null;
            using var doc = JsonDocument.Parse(json.AsMemory(start));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int? error = root.TryGetProperty("lsarError", out var e) && e.TryGetInt32(out int number) ? number : null;
            bool hasContents = root.TryGetProperty("lsarContents", out var contents) && contents.ValueKind == JsonValueKind.Array;
            if (!hasContents && error is null) return null;

            var entries = new List<UnarEntry>();
            if (hasContents)
                foreach (var item in contents.EnumerateArray())
                    entries.Add(Entry(item));

            bool encrypted = false, corrupted = false;
            var volumes = new List<string>();
            if (root.TryGetProperty("lsarProperties", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                encrypted = Set(p, "XADIsEncrypted");
                corrupted = Set(p, "XADIsCorrupted");
                if (p.TryGetProperty("XADVolumes", out var v) && v.ValueKind == JsonValueKind.Array)
                    volumes.AddRange(v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            }
            return new UnarListing(entries, error,
                encrypted || entries.Any(x => x.Encrypted), corrupted || entries.Any(x => x.Corrupted), volumes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static UnarEntry Entry(JsonElement item)
    {
        string? name = item.TryGetProperty("XADFileName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        long size = item.TryGetProperty("XADFileSize", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long value) && value >= 0
            ? value : -1;
        // Unix-Dateiart in den Rechten: erlaubt sind nur gewöhnliche Dateien und Ordner (0 = nicht angegeben).
        long type = item.TryGetProperty("XADPosixPermissions", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out long mode)
            ? mode & 0xF000 : 0;
        bool link = Set(item, "XADIsLink") || Set(item, "XADIsHardLink") || item.TryGetProperty("XADLinkDestination", out _)
            || Set(item, "RAR5RedirectionType") || Set(item, "XADIsCharacterDevice") || Set(item, "XADIsBlockDevice") || Set(item, "XADIsFIFO")
            || type is not (0 or 0x8000 or 0x4000);
        return new UnarEntry(name, size, Set(item, "XADIsDirectory"), link, Set(item, "XADIsEncrypted"), Set(item, "XADIsCorrupted"),
            Set(item, "XADIsResourceFork"));
    }

    /// <summary>Gesetzt: vorhanden und weder 0 noch false.</summary>
    private static bool Set(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => !v.TryGetInt64(out long n) || n != 0,
            _ => false,
        };
}

internal enum UnarProblem
{
    Password,
    Damaged,
    Unsupported,
    Other,
}

/// <summary>
/// Meldungen von <c>unar</c> und <c>lsar</c>. Dateinamen stehen in der Ausgabe nur in eingerückten Zeilen vor dem Ergebnis, und
/// Namen mit Zeilenumbrüchen sind schon vorher abgelehnt. Ausgewertet werden deshalb nur ganze Zeilen ohne Einrückung und der
/// Grund am Zeilenende: Ein Name wie <c>wrong password.txt</c> ändert nichts.
/// </summary>
internal static class UnarErrors
{
    private const string Request = "This archive requires a password to unpack. Use the -p option to provide one.";

    private static readonly Regex Entry = new(@"^  .*\.\.\. Failed! \((?<why>[^()]*(?:\([^()]*\))?[^()]*)\)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Parsing = new(@"^Archive parsing failed! \((?<why>.*?)\.?\)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool NeedsPassword(string output) => Lines(output).Any(l =>
        l == Request
        // unar setzt den Pfad des Archivs davor, oder (eingerückt) den Namen des Eintrags, an dem es nicht weitergeht.
        || (!l.StartsWith(' ') && l.EndsWith(": " + Request, StringComparison.Ordinal))
        || (l.StartsWith("  ", StringComparison.Ordinal) && l.EndsWith(")... " + Request, StringComparison.Ordinal)));

    /// <summary>Die Ursache eines gescheiterten Laufs, dazu der Grund im Wortlaut des Programms (ohne Dateinamen, null = keiner gefunden).</summary>
    public static (UnarProblem Problem, string? Reason) Classify(string output)
    {
        if (NeedsPassword(output)) return (UnarProblem.Password, null);
        var reasons = new List<string>();
        bool unknownFormat = false;
        foreach (string line in Lines(output))
        {
            if (Entry.Match(line) is { Success: true } e) reasons.Add(e.Groups["why"].Value);
            else if (Parsing.Match(line) is { Success: true } p) reasons.Add(p.Groups["why"].Value);
            else if (!line.StartsWith(' ') && line.EndsWith("Couldn't recognize the archive format.", StringComparison.Ordinal)) unknownFormat = true;
        }
        if (reasons.Find(r => r.Contains("password", StringComparison.OrdinalIgnoreCase)) is { } password) return (UnarProblem.Password, password);
        if (reasons.Find(r => r.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || r.Contains("unsupported", StringComparison.OrdinalIgnoreCase)) is { } unsupported)
            return (UnarProblem.Unsupported, unsupported);
        string[] damage = { "checksum", "decrunch", "read more data", "unknown error", "illegal data" };
        if (reasons.Find(r => damage.Any(d => r.Contains(d, StringComparison.OrdinalIgnoreCase))) is { } damaged) return (UnarProblem.Damaged, damaged);
        if (unknownFormat) return (UnarProblem.Damaged, null);
        return (UnarProblem.Other, reasons.Count > 0 ? reasons[0] : null);
    }

    private static IEnumerable<string> Lines(string output) => output.Split('\n').Select(l => l.TrimEnd('\r'));
}
