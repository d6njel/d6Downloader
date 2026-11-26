using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace D6Downloader.Packages;

/// <summary>Stand eines Archivteils vor dem Entpacken. <see cref="Length"/> -1: Die Datei war nicht lesbar und wird nie angefasst.</summary>
public sealed record PartStamp(string Path, long Length, DateTime WriteUtc);

/// <summary>
/// Verschiebt die Teile eines vollständig entpackten Archivs in den Papierkorb. Angefasst werden nur die vorher gestempelten
/// Dateien, und nur, wenn sie seitdem unverändert sind. Hier wird nie endgültig gelöscht.
/// </summary>
public static class ArchiveCleanup
{
    /// <summary>Vor dem Entpacken aufrufen. Jede Datei kommt einmal vor.</summary>
    public static IReadOnlyList<PartStamp> Stamp(IEnumerable<string> paths)
    {
        var stamps = new List<PartStamp>();
        foreach (string path in paths.Where(p => !string.IsNullOrEmpty(p)).Select(FullPath).Distinct(StringComparer.Ordinal))
        {
            var stamp = new PartStamp(path, -1, default);
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) stamp = new PartStamp(path, info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
            stamps.Add(stamp);
        }
        return stamps;
    }

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return path; }
    }

    /// <summary>
    /// Nur nach <see cref="ExtractState.Done"/> ohne Fehler und mit mindestens einer entpackten Datei, sonst (0, 0).
    /// </summary>
    /// <param name="trash">Verschiebt eine Datei in den Papierkorb (siehe <c>ITrash</c>).</param>
    public static (int Removed, int Kept) Remove(IReadOnlyList<PartStamp> stamps, ExtractResult result, Action<string> trash,
        CancellationToken ct)
    {
        if (result is not { State: ExtractState.Done, Failure: ExtractFailure.None, Files: > 0 }) return (0, 0);
        int removed = 0;
        foreach (var stamp in stamps)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var info = new FileInfo(stamp.Path);
                if (!info.Exists || info.LinkTarget is not null || info.Length != stamp.Length || info.LastWriteTimeUtc != stamp.WriteUtc)
                    continue;
                trash(stamp.Path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return (removed, stamps.Count - removed);
    }
}
