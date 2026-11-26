using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using D6Downloader.Platform;

namespace D6Downloader.Packages;

/// <param name="EntryPath">Datei, mit der der Entpacker beginnt (siehe <see cref="ExtractPlanner.EntryIndex"/>).</param>
/// <param name="TargetDir">Paketordner, hierhin wird entpackt.</param>
/// <param name="PartPaths">Alle Teile des Archivs.</param>
/// <param name="Passwords">Passwörter in der Reihenfolge, in der sie nach dem Versuch ohne Passwort probiert werden (siehe <see cref="PasswordRules.Plan"/>).</param>
public sealed record ExtractJob(string EntryPath, string TargetDir, ArchiveType Type, bool MultiPart, IReadOnlyList<string> PartPaths,
    IReadOnlyList<string>? Passwords = null);

/// <param name="Detail">Kurzer Text des Entpackers bei <see cref="ExtractFailure.ToolFailed"/> und <see cref="ExtractFailure.Io"/> (höchstens 200 Zeichen), bei <see cref="ExtractFailure.NoSpace"/> „nötig|frei“ in Bytes, bei <see cref="ExtractState.NeedPassword"/> die Zahl der probierten Passwörter (fehlt bei 0).</param>
/// <param name="UsedPassword">Das Passwort, mit dem entpackt wurde (nur im Speicher, null = keins nötig).</param>
/// <param name="Unverified">Entpackt, aber die Vollständigkeit ist nicht belegt (<c>bsdtar</c> bei ZIP und RAR, oder ein RAR, dessen Struktur sie nicht zeigt, siehe <see cref="RarCompleteness"/>): Das Archiv bleibt dann liegen.</param>
public sealed record ExtractResult(ExtractState State, ExtractFailure Failure = ExtractFailure.None, string? Detail = null, int Files = 0,
    string? UsedPassword = null, bool Unverified = false);

/// <summary>
/// Entpackt ein Paket in den Paketordner. Die Suche nach 7-Zip und <c>bsdtar</c> geschieht erst hier (über die Delegaten), nicht
/// beim Start. Programme starten nur mit Argumentliste, nie über eine Shell, mit niedriger Priorität. Es wird immer zuerst in einen
/// frischen Ordner im Paketordner entpackt, dort geprüft (keine Verknüpfungen, nichts außerhalb) und erst dann verschoben, wobei
/// nie etwas Vorhandenes überschrieben wird. Läuft synchron, der Aufrufer startet es im Thread-Pool.
/// </summary>
public sealed partial class ArchiveExtractor
{
    internal const string StagingPrefix = ".ydl-extract-";
    private const int MaxOutput = 4 * 1024 * 1024;

    private readonly Func<string?> _findSevenZip;
    private readonly Func<string?> _findTar;
    private readonly Func<string, long?> _freeSpace;
    private readonly TimeSpan _attemptTimeout;

    private readonly Func<string?> _findUnar;
    private readonly Func<string?> _findLsar;
    private readonly bool _allowUnarPassword;

    /// <summary>Zeitlimit je Aufruf eines Entpackers (je Versuch mit einem Passwort), damit ein hängendes Programm nicht ewig blockiert.</summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromMinutes(30);

    /// <param name="allowUnarPassword">Darf ein Passwort auf die Befehlszeile von <c>unar</c>/<c>lsar</c> (einziger Weg dieser Programme, in der Prozessliste sichtbar)?</param>
    public ArchiveExtractor(Func<string?> findSevenZip, Func<string?> findTar, Func<string, long?>? freeSpace = null, TimeSpan? attemptTimeout = null,
        Func<string?>? findUnar = null, Func<string?>? findLsar = null, bool allowUnarPassword = false)
    {
        _findUnar = findUnar ?? (() => null);
        _findLsar = findLsar ?? (() => null);
        _allowUnarPassword = allowUnarPassword;
        _findSevenZip = findSevenZip;
        _findTar = findTar;
        _freeSpace = freeSpace ?? PlatformServices.FreeSpace.Query;
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
    }

    public ExtractResult Run(ExtractJob job, CancellationToken ct)
    {
        try
        {
            return RunCore(job, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new(ExtractState.Failed, ExtractFailure.Timeout);
        }
        catch (Win32Exception ex)
        {
            return new(ExtractState.Failed, ExtractFailure.ToolFailed, Short(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ExtractState.Failed, ExtractFailure.Io, Short(ex.Message));
        }
    }

    private ExtractResult RunCore(ExtractJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (job.PartPaths.Count == 0 || job.PartPaths.Any(p => !File.Exists(p)) || !File.Exists(job.EntryPath))
            return new(ExtractState.Failed, ExtractFailure.MissingParts);

        var zipInfo = default(ZipInfo);
        if (job.Type == ArchiveType.Zip && !job.MultiPart)
        {
            using var fs = new FileStream(job.EntryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            zipInfo = ZipProbe.Scan(fs);
        }

        // Erst jetzt wird gesucht: der Startpfad bleibt davon frei.
        string? sevenZip = _findSevenZip();
        string? tar = _findTar();
        string? unar = null, lsar = null;
        if (job.Type == ArchiveType.Rar)
        {
            unar = _findUnar();
            lsar = _findLsar();
            // Ein 7-Zip ohne RAR wird gar nicht erst gestartet: Es meldete ein gutes Archiv als unlesbar.
            if (sevenZip is not null && !CanReadRar(sevenZip, ct)) sevenZip = null;
        }
        var chain = ExtractPlanner.Chain(job.Type, job.MultiPart, zipInfo.Encrypted, zipInfo.UnsupportedMethod is not null,
            sevenZip is not null, tar is not null, unar is not null && lsar is not null);
        if (chain.Count == 0)
        {
            // Kein Entpacker: Ist das Verfahren bekannt, steht es in der Meldung, sonst „7-Zip nötig“ bzw. „unar nötig“.
            return zipInfo.UnsupportedName is { } name
                ? new(ExtractState.Failed, ExtractFailure.UnsupportedMethod, name + "|install")
                : new(ExtractPlanner.Missing(job.Type));
        }

        bool created = !Directory.Exists(job.TargetDir);
        Directory.CreateDirectory(job.TargetDir);
        CleanStaleStaging(job.TargetDir);
        string staging = Path.Combine(job.TargetDir, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string? usedPassword = null;
            var failures = new List<ExtractResult>();
            bool done = false;
            bool unverified = false;
            for (int index = 0; index < chain.Count; index++)
            {
                var kind = chain[index];
                ct.ThrowIfCancellationRequested();
                // bsdtar meldet bei beschädigten ZIP und RAR teils Erfolg: Ist die Datei wirklich von dieser Art, überstimmt es
                // ein „beschädigt“ nie. Ist sie etwas anderes (etwa ein 7z mit der Endung .zip), darf es sie noch lesen.
                if (kind == ExtractorKind.Tar && failures.Any(f => f.Failure == ExtractFailure.Corrupt) && HasSignature(job)) break;
                if (failures.Count > 0) ClearDirectory(staging); // Reste des vorigen Versuchs
                ExtractResult? stop;
                try
                {
                    stop = kind switch
                    {
                        ExtractorKind.DotNet => ExtractZip(job, staging, ct),
                        ExtractorKind.SevenZip => ExtractSevenZip(sevenZip!, job, staging, ct, out usedPassword),
                        ExtractorKind.Unar => ExtractUnar(unar!, lsar!, job, staging, ct, out usedPassword),
                        _ => ExtractTar(tar!, job, staging, ct),
                    };
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    stop = FromException(ex);
                }
                catch (Win32Exception ex) when (kind != chain[^1])
                {
                    stop = new(ExtractState.Failed, ExtractFailure.ToolFailed, Short(ex.Message)); // Programm nicht startbar: der nächste darf es versuchen
                }
                if (stop is null)
                {
                    done = true;
                    unverified = kind == ExtractorKind.Tar && job.Type != ArchiveType.SevenZip;
                    break;
                }
                if (stop.Failure == ExtractFailure.PasswordNeedsTool)
                {
                    // Nur 7-Zip (Passwort über die Standardeingabe) kann hier noch helfen, bsdtar nie.
                    if (!chain.Skip(index + 1).Contains(ExtractorKind.SevenZip)) return stop;
                    failures.Add(stop);
                    continue;
                }
                if (kind == ExtractorKind.Tar && job.Type == ArchiveType.Rar && stop.State == ExtractState.NeedPassword)
                {
                    // bsdtar bekommt nie ein Passwort: Danach zu fragen, hülfe nicht.
                    if (failures.Count == 0) return new(ExtractState.NeedRarTool);
                    break;
                }
                if (!ExtractPlanner.ShouldTryNext(stop)) return stop;
                failures.Add(stop);
            }
            if (!done) return ExtractPlanner.FinalFailure(failures, zipInfo.UnsupportedName);

            MakeAccessible(staging);
            var files = ValidateStaging(staging, ct);
            if (files is null) return new(ExtractState.Failed, ExtractFailure.Link);
            ct.ThrowIfCancellationRequested();
            // RAR meldet fehlende Dateien am Ende nicht von selbst, auch wenn es anders heißt: dann muss die Struktur es belegen.
            if (!unverified && (job.Type == ArchiveType.Rar || RarCompleteness.IsRar(job.EntryPath)))
                unverified = !RarCompleteness.IsProven(job.PartPaths);
            int moved = MoveOut(staging, job.TargetDir, ct);
            return new(ExtractState.Done, Files: moved, UsedPassword: usedPassword, Unverified: unverified);
        }
        finally
        {
            TryDelete(staging);
            // Ein Zielordner, den erst dieser Lauf angelegt hat und der leer blieb (Fehler, Passwort, Abbruch), soll nicht herumliegen.
            if (created) TryDeleteIfEmpty(job.TargetDir);
        }
    }

    /// <summary>Beginnt die Datei so, wie es zu ihrer Art gehört?</summary>
    private static bool HasSignature(ExtractJob job)
    {
        byte[] magic = job.Type switch
        {
            ArchiveType.Zip => "PK"u8.ToArray(),
            ArchiveType.Rar => "Rar!"u8.ToArray(),
            _ => new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF },
        };
        using var file = new FileStream(job.EntryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[magic.Length];
        return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.AsSpan().SequenceEqual(magic);
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- Platz und Pfade

    /// <summary>Null = passt. Sonst das Ergebnis „zu wenig Platz“ mit „nötig|frei“ im Detail.</summary>
    private ExtractResult? CheckSpace(string dir, long uncompressed)
    {
        if (_freeSpace(dir) is not { } free) return null; // nicht zu ermitteln (etwa ein ungewöhnlicher Pfad): nicht blockieren
        if (SpaceCheck.Enough(uncompressed, free)) return null;
        return new(ExtractState.Failed, ExtractFailure.NoSpace, SpaceCheck.Required(uncompressed) + "|" + free);
    }

    private static long ArchiveBytes(ExtractJob job) => job.PartPaths.Sum(p => new FileInfo(p).Length);

    // ---------------------------------------------------------------- .NET (ZIP)

    private ExtractResult? ExtractZip(ExtractJob job, string staging, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(job.EntryPath);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.Length == 0) continue;
            if (PathGuard.Resolve(staging, e.FullName) is null) return new(ExtractState.Failed, ExtractFailure.UnsafePath);
            total += Math.Max(0, e.Length);
        }
        if (CheckSpace(job.TargetDir, total) is { } noSpace) return noSpace;

        var buffer = new byte[81920];
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            bool folder = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            // Daten, die nirgends landen würden (Ordner oder Eintrag ohne Namen), gelten als Schaden.
            if ((folder || e.FullName.Length == 0) && e.Length != 0) throw new InvalidDataException("Eintrag ohne Datei trägt Daten");
            if (e.FullName.Length == 0) continue;
            string full = PathGuard.Resolve(staging, e.FullName)!;
            if (folder)
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var input = e.Open();
            using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long written = 0;
            uint crc = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += n;
                if (written > e.Length) throw new InvalidDataException("Eintrag größer als angegeben"); // Archivbombe mit falscher Größe
                output.Write(buffer, 0, n);
                crc = Crc32.Append(crc, buffer, n);
                ct.ThrowIfCancellationRequested();
            }
            if (written != e.Length) throw new InvalidDataException("Eintrag kürzer als angegeben"); // abgeschnittenes Archiv
            if (crc != e.Crc32) throw new InvalidDataException("Prüfsumme stimmt nicht"); // beschädigter Inhalt
        }
        return null;
    }

    // ---------------------------------------------------------------- 7-Zip

    internal sealed class Listed
    {
        public string Path = "";
        public long Size = -1;
        public bool Encrypted;
    }

    /// <summary>
    /// Erst ohne Passwort, dann der Reihe nach die Passwörter des Jobs (höchstens <see cref="PasswordRules.MaxCandidates"/>).
    /// Null = entpackt, sonst das Ergebnis, mit dem das Entpacken endet. Das Passwort geht nie auf die Kommandozeile (dort sähen
    /// es andere Prozesse desselben Benutzers), sondern über die Standardeingabe von 7-Zip: Ohne <c>-p</c> fragt 7-Zip nur dann
    /// danach, wenn ein Passwort gebraucht wird, und liest es von dort.
    /// </summary>
    private ExtractResult? ExtractSevenZip(string exe, ExtractJob job, string staging, CancellationToken ct, out string? usedPassword)
    {
        var plan = PasswordRules.Plan(job.Passwords, null, null);
        var found = PasswordSearch.Run(plan, pw => SevenZipAttempt(exe, job, staging, pw, ct), ct);
        usedPassword = found.Password;
        if (found.Last.Outcome == AttemptOutcome.Success) return null;
        if (found.Last.Result is { } stop) return stop;
        return new ExtractResult(ExtractState.NeedPassword, Detail: found.Tried > 0 ? found.Tried.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private Attempt SevenZipAttempt(string exe, ExtractJob job, string staging, string? password, CancellationToken ct)
    {
        // Verzeichnis lesen: Namen prüfen, Größe summieren, Passwort erkennen, bevor etwas auf die Platte kommt.
        var (code, output) = RunProcess(exe, new[] { "l", "-slt", "-ba", "-y", "-sccUTF-8", "--", job.EntryPath }, ct, password);
        if (code != 0) return Fail(output, password);

        var entries = ParseSevenZipList(output);
        // Das Verzeichnis liest sich auch ohne Passwort, solange nur die Inhalte verschlüsselt sind: Ohne Passwort kann es nicht klappen.
        if (password is null && entries.Any(e => e.Encrypted)) return new(AttemptOutcome.WrongPassword);
        long total = 0;
        foreach (var e in entries)
        {
            if (e.Path.Length == 0) continue;
            if (PathGuard.Resolve(staging, e.Path) is null) return Stop(new(ExtractState.Failed, ExtractFailure.UnsafePath));
            if (e.Size > 0) total += e.Size;
        }
        if (entries.Count == 0 || entries.Any(e => e.Size < 0 && !e.Path.EndsWith('\\') && !e.Path.EndsWith('/')))
            total = Math.Max(total, SpaceCheck.Estimate(ArchiveBytes(job)));
        if (CheckSpace(job.TargetDir, total) is { } noSpace) return Stop(noSpace);

        ClearDirectory(staging); // Reste eines Versuchs mit falschem Passwort
        (code, output) = RunProcess(exe, new[] { "x", "-o" + staging, "-aou", "-y", "-bd", "-sccUTF-8", "--", job.EntryPath }, ct, password);
        return code == 0 ? new(AttemptOutcome.Success) : Fail(output, password);
    }

    private static Attempt Stop(ExtractResult r) => new(AttemptOutcome.Stop, r);

    /// <summary>
    /// Ein Fehler von 7-Zip: Sieht er nach falschem oder fehlendem Passwort aus, kommt das nächste dran. Ein Passwort steht nie in
    /// einem Ergebnis, auch wenn das Programm es in einer Meldung wiederholt (erst hier ersetzt, nicht beim Lesen der Ausgabe:
    /// Ein kurzes Passwort würde sonst auch Dateinamen im Verzeichnis zerstören).
    /// </summary>
    private static Attempt Fail(string output, string? password)
    {
        if (!string.IsNullOrEmpty(password)) output = output.Replace(password, "***", StringComparison.Ordinal);
        return LooksLikePasswordProblem(output) ? new(AttemptOutcome.WrongPassword) : Stop(Failed(output));
    }

    private static void ClearDirectory(string dir)
    {
        MakeAccessible(dir);
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
    }

    internal static List<Listed> ParseSevenZipList(string output)
    {
        var list = new List<Listed>();
        Listed? current = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            string key = line[..eq], value = line[(eq + 3)..];
            if (key == "Path")
            {
                current = new Listed { Path = value };
                list.Add(current);
            }
            else if (current is null) continue;
            else if (key == "Size" && long.TryParse(value, out long size)) current.Size = size;
            else if (key == "Encrypted" && value == "+") current.Encrypted = true;
        }
        return list;
    }

    // ---------------------------------------------------------------- bsdtar

    private static readonly Regex TarLine = new(
        @"^[-dlhcbps][-rwxsStT]{9}\S*\s+\d+\s+.*?\s+(?<size>\d+)\s+\w{3}\s+\d+\s+(?:\d{1,2}:\d{2}|\d{4})\s+(?<name>.+?)(?: link to .*)?$",
        RegexOptions.CultureInvariant);

    private ExtractResult? ExtractTar(string exe, ExtractJob job, string staging, CancellationToken ct)
    {
        var (code, output) = RunProcess(exe, new[] { "-tvf", job.EntryPath }, ct);
        if (code != 0) return Failed(output);

        long total = 0;
        bool known = true;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = TarLine.Match(line);
            if (!m.Success || !long.TryParse(m.Groups["size"].Value, out long size)) { known = false; continue; }
            if (PathGuard.Resolve(staging, m.Groups["name"].Value) is null) return new(ExtractState.Failed, ExtractFailure.UnsafePath);
            total += size;
        }
        if (!known || total == 0) total = Math.Max(total, SpaceCheck.Estimate(ArchiveBytes(job)));
        if (CheckSpace(job.TargetDir, total) is { } noSpace) return noSpace;

        (code, output) = RunProcess(exe, new[] { "-xf", job.EntryPath, "-C", staging }, ct);
        return code == 0 ? null : Failed(output);
    }

    // ---------------------------------------------------------------- Ergebnis prüfen und verschieben

    /// <summary>
    /// Alles unter <paramref name="staging"/> auflisten, ohne Verknüpfungen zu folgen. Null, wenn eine Verknüpfung (Symlink, Junction)
    /// darunter liegt: Die Datei hinter ihr könnte außerhalb liegen. Sonst die relativen Pfade der Dateien.
    /// </summary>
    internal static List<string>? ValidateStaging(string staging, CancellationToken ct)
    {
        string root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(staging);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null) return null;
                if (!Path.GetFullPath(info.FullName).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                if (info is DirectoryInfo) stack.Push(info.FullName);
                else files.Add(Path.GetRelativePath(staging, info.FullName));
            }
        }
        return files;
    }

    /// <summary>Verschiebt alles aus dem Staging-Ordner ins Ziel. Vorhandenes bleibt: Die neue Datei bekommt <c> (1)</c>, <c> (2)</c> …</summary>
    private static int MoveOut(string staging, string target, CancellationToken ct)
    {
        int count = 0;
        foreach (string rel in ValidateStaging(staging, ct)!)
        {
            ct.ThrowIfCancellationRequested();
            string? dest = PathGuard.Resolve(target, rel) ?? throw new IOException(Loc.T("Extract.Detail.UnsafePath", rel));
            string dir = Path.GetDirectoryName(dest)!;
            Directory.CreateDirectory(dir);
            File.Move(Path.Combine(staging, rel), UniquePath(dest));
            count++;
        }
        return count;
    }

    internal static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException(Loc.T("Extract.Detail.NoFreeName", path));
    }

    private static void CleanStaleStaging(string dir)
    {
        try
        {
            foreach (string old in Directory.EnumerateDirectories(dir, StagingPrefix + "*"))
                TryDelete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            MakeAccessible(dir);
            Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Archive können Ordner ohne Rechte mitbringen, die sich sonst weder prüfen noch wegräumen lassen: dem Benutzer Lesen,
    /// Schreiben und Betreten geben. Verknüpfungen wird nie gefolgt.
    /// </summary>
    private static void MakeAccessible(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode own = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                if (dir.LinkTarget is not null) continue;
                if ((dir.UnixFileMode & own) != own) dir.UnixFileMode |= own;
                foreach (var child in dir.EnumerateDirectories())
                    if (child.LinkTarget is null) stack.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Was sich nicht öffnen lässt, meldet der nächste Zugriff.
            }
        }
    }

    // ---------------------------------------------------------------- Prozesse

    private static bool LooksLikePasswordProblem(string output) =>
        Regex.IsMatch(output, "password|passphrase|encrypted", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static ExtractResult Failed(string output)
    {
        if (LooksLikePasswordProblem(output)) return new(ExtractState.NeedPassword);
        string? line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            ?? output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        // Ein erkanntes Verfahren oder ein beschädigtes Archiv bekommt seinen eigenen Grund (ohne Text des Programms).
        return ExtractErrors.Classify(output) is var reason and (ExtractFailure.UnsupportedMethod or ExtractFailure.Corrupt)
            ? new(ExtractState.Failed, reason)
            : new(ExtractState.Failed, ExtractFailure.ToolFailed, line is null ? null : Short(line));
    }

    /// <summary>Eine Ausnahme von <c>System.IO.Compression</c>: Verfahren unbekannt oder Archiv beschädigt.</summary>
    internal static ExtractResult FromException(Exception ex) =>
        new(ExtractState.Failed, ExtractErrors.Classify(ex.Message) == ExtractFailure.UnsupportedMethod || ex is NotSupportedException
            ? ExtractFailure.UnsupportedMethod : ExtractFailure.Corrupt);

    private static string Short(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>Startet ein Programm ohne Shell, mit Argumentliste und niedriger Priorität. Der Abbruch beendet es samt Kindprozessen. Beide Ausgaben in einem Text.</summary>
    private (int Code, string Output) RunProcess(string exe, IEnumerable<string> args, CancellationToken ct, string? password = null)
    {
        var output = new StringBuilder();
        int code = Execute(exe, args, ct, password, output, output, MaxOutput, _attemptTimeout);
        return (code, output.ToString());
    }

    private int Execute(string exe, IEnumerable<string> args, CancellationToken ct, string? password,
        StringBuilder output, StringBuilder errors, int limit, TimeSpan limitTime)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = "/", // neutraler Ordner, nie der Download-Ordner
        };
        // Meldungen und Listen der Werkzeuge werden ausgewertet: feste Sprache, aber UTF-8 für Dateinamen.
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment.Remove("LANGUAGE");
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new IOException(Loc.T("Extract.Detail.NotStarted"));
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }

        // Ein Passwort geht über die Standardeingabe, nie auf die Kommandozeile. Ohne Passwort wird sie sofort geschlossen:
        // Eine Rückfrage des Programms scheitert dann sofort.
        try
        {
            if (password is not null) process.StandardInput.WriteLine(password);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Das Programm hat sich schon beendet.
        }

        var readers = new[]
        {
            new Thread(() => Pump(process.StandardOutput, output, limit)) { IsBackground = true },
            new Thread(() => Pump(process.StandardError, errors, limit)) { IsBackground = true },
        };
        foreach (var t in readers) t.Start();

        using var timeout = new CancellationTokenSource(limitTime);
        using var reg = ct.Register(Kill);
        using var regTimeout = timeout.Token.Register(Kill);
        void Kill()
        {
            try { process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
        process.WaitForExit();
        foreach (var t in readers) t.Join(2000);
        ct.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new TimeoutException();
        // Sperren, damit ein noch nachlaufender Leser (Enkelprozess hält die Leitung offen) fertig angehängt hat.
        lock (output) { }
        lock (errors) { }
        return process.ExitCode;
    }

    private static void Pump(StreamReader reader, StringBuilder into, int limit)
    {
        var buffer = new char[8192];
        int n;
        try
        {
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                lock (into)
                    if (into.Length < limit) into.Append(buffer, 0, n);
        }
        catch (IOException)
        {
        }
    }
}
