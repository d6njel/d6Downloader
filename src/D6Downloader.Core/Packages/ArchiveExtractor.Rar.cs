using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace D6Downloader.Packages;

/// <summary>Der RAR-Weg: <c>unar</c> mit <c>lsar</c>, und die Frage, ob ein 7-Zip überhaupt RAR kann.</summary>
public sealed partial class ArchiveExtractor
{
    /// <summary>Ein Verzeichnis in JSON ist je Eintrag rund 1 KB groß, deshalb mehr als sonst.</summary>
    private const int MaxUnarOutput = 64 * 1024 * 1024;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly ConcurrentDictionary<(string Path, long Changed, long Length), bool> RarSupport = new();

    /// <summary>Fragt einmal je Programmdatei mit <c>7z i</c> nach den Formaten. Ohne klare Antwort gilt: kein RAR.</summary>
    private bool CanReadRar(string sevenZip, CancellationToken ct)
    {
        try
        {
            var file = new FileInfo(sevenZip);
            var key = (sevenZip, file.Exists ? file.LastWriteTimeUtc.Ticks : 0, file.Exists ? file.Length : 0);
            if (RarSupport.TryGetValue(key, out bool known)) return known;
            var output = new StringBuilder();
            var limit = _attemptTimeout < ProbeTimeout ? _attemptTimeout : ProbeTimeout;
            if (Execute(sevenZip, new[] { "i" }, ct, null, output, output, MaxOutput, limit) != 0) return false;
            return RarSupport[key] = ToolLocator.SevenZipSupportsRar(output.ToString());
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Erst ohne Passwort. Weitere Versuche mit Passwörtern nur, wenn es erlaubt ist: <c>unar</c> und <c>lsar</c> nehmen es nur
    /// mit <c>-p</c> auf der Befehlszeile an. Null = entpackt.
    /// </summary>
    private ExtractResult? ExtractUnar(string unar, string lsar, ExtractJob job, string staging, CancellationToken ct, out string? usedPassword)
    {
        IReadOnlyList<string?> plan = _allowUnarPassword ? PasswordRules.Plan(job.Passwords, null, null) : new string?[] { null };
        var found = PasswordSearch.Run(plan, pw => UnarAttempt(unar, lsar, job, staging, pw, ct), ct);
        usedPassword = found.Password;
        if (found.Last.Outcome == AttemptOutcome.Success) return null;
        if (found.Last.Result is { } stop) return stop;
        if (!_allowUnarPassword) return new(ExtractState.Failed, ExtractFailure.PasswordNeedsTool);
        return new(ExtractState.NeedPassword, Detail: found.Tried > 0 ? found.Tried.ToString(CultureInfo.InvariantCulture) : null);
    }

    private Attempt UnarAttempt(string unar, string lsar, ExtractJob job, string staging, string? password, CancellationToken ct)
    {
        var secret = password is null ? Array.Empty<string>() : new[] { "-p", password };

        // Erst das Verzeichnis: Namen, Verknüpfungen, Vollständigkeit und Größe, bevor etwas auf die Platte kommt.
        var json = new StringBuilder();
        var errors = new StringBuilder();
        var listArgs = new[] { "-j", "-jss", "-nr" }.Concat(secret).Append(job.EntryPath);
        Execute(lsar, listArgs, ct, null, json, errors, MaxUnarOutput, _attemptTimeout);
        string listText = json.ToString();
        // Die Bitte um ein Passwort steht in eigenen Zeilen. Im JSON gibt es in Namen keine Zeilenumbrüche.
        if (UnarErrors.NeedsPassword(listText) || UnarErrors.NeedsPassword(errors.ToString())) return new(AttemptOutcome.WrongPassword);

        bool multiPart = job.MultiPart;
        if (UnarListing.Parse(listText) is not { } listing)
            return Stop(new(ExtractState.Failed, ExtractFailure.Corrupt));
        multiPart |= listing.Volumes.Count > 1;

        long total = 0;
        bool sizeUnknown = false;
        foreach (var e in listing.Entries)
        {
            // Steuerzeichen (auch Zeilenumbrüche) lehnt PathGuard mit ab.
            if (e.Name is null || PathGuard.Resolve(staging, e.Name) is null) return Stop(new(ExtractState.Failed, ExtractFailure.UnsafePath));
            // Vor dem Start abbrechen: unar legt Verknüpfungen an und könnte durch sie hindurch schreiben.
            if (e.IsLink) return Stop(new(ExtractState.Failed, ExtractFailure.Link));
            if (e.IsDirectory) continue;
            if (e.Size < 0) sizeUnknown = true;
            else total = e.Size > long.MaxValue - total ? long.MaxValue : total + e.Size;
        }

        // Mit falschem Passwort lässt sich ein verschlüsseltes Verzeichnis nicht lesen.
        if (listing.Error == UnarListing.PasswordError || (password is not null && listing.Error is not null && listing.Entries.Count == 0))
            return new(AttemptOutcome.WrongPassword);
        if (listing.Error == UnarListing.NotSupportedError) return Stop(new(ExtractState.Failed, ExtractFailure.UnsupportedMethod));
        // Fehlt ein Teil, ist das Verzeichnis unvollständig oder sogar leer (unar meldet dann trotzdem Erfolg).
        if (listing.Error is not null || listing.Corrupted || listing.HasVolumeGap || listing.Entries.Count == 0)
            return Stop(new(ExtractState.Failed, multiPart ? ExtractFailure.MissingParts : ExtractFailure.Corrupt));
        if (listing.Encrypted && password is null) return new(AttemptOutcome.WrongPassword);

        if (sizeUnknown) total = Math.Max(total, SpaceCheck.Estimate(ArchiveBytes(job)));
        if (CheckSpace(job.TargetDir, total) is { } noSpace) return Stop(noSpace);

        ClearDirectory(staging); // Reste eines Versuchs mit falschem Passwort
        var output = new StringBuilder();
        // -D: kein zusätzlicher Ordner, -nr: Archive im Archiv bleiben Archive, -r: nie überschreiben, -k skip: keine Mac-Zusatzdateien.
        var args = new[] { "-o", staging, "-D", "-nr", "-r", "-k", "skip" }.Concat(secret).Append(job.EntryPath);
        int code = Execute(unar, args, ct, null, output, output, MaxUnarOutput, _attemptTimeout);
        if (code == 0)
        {
            MakeAccessible(staging);
            // Verknüpfungen meldet die Prüfung des Staging-Ordners; sonst muss jede Datei des Verzeichnisses angekommen sein.
            var files = ValidateStaging(staging, ct);
            if (files is not null && files.Count < listing.Entries.Count(e => !e.IsDirectory && !e.ResourceFork))
                return Stop(new(ExtractState.Failed, ExtractFailure.Corrupt));
            return new(AttemptOutcome.Success);
        }

        var (problem, reason) = UnarErrors.Classify(output.ToString());
        if (problem == UnarProblem.Password) return new(AttemptOutcome.WrongPassword);
        // Bei verschlüsselten Archiven sieht ein falsches Passwort (vor allem bei RAR 4) wie Beschädigung aus.
        if (problem == UnarProblem.Damaged && listing.Encrypted) return new(AttemptOutcome.WrongPassword);
        if (!string.IsNullOrEmpty(password) && reason is not null) reason = reason.Replace(password, "***", StringComparison.Ordinal);
        return Stop(problem switch
        {
            UnarProblem.Damaged => new(ExtractState.Failed, ExtractFailure.Corrupt),
            UnarProblem.Unsupported => new(ExtractState.Failed, ExtractFailure.UnsupportedMethod),
            _ => new(ExtractState.Failed, ExtractFailure.ToolFailed, reason is null ? null : Short(reason)),
        });
    }
}
