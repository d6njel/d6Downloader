using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using D6Downloader.Packages;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Der Papierkorb ist hier immer nur eine Liste: Es wird nichts verschoben.</summary>
public class ArchiveCleanupTests
{
    private static readonly ExtractResult Done = new(ExtractState.Done, Files: 1);

    private static string[] Files(TempDir dir, params string[] names) => names.Select(n =>
    {
        string path = dir.File(n);
        File.WriteAllText(path, n);
        return path;
    }).ToArray();

    [Theory]
    [InlineData("a.part1.rar", "a.part2.rar", "a.part3.rar")]
    [InlineData("a.7z.001", "a.7z.002", "a.7z.003")]
    [InlineData("a.z01", "a.z02", "a.zip")]
    public void All_parts_are_moved_and_nothing_else(string first, string second, string third)
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, first, second, third);
        string neighbour = Files(dir, "a.nfo")[0];
        var moved = new List<string>();

        var counts = ArchiveCleanup.Remove(ArchiveCleanup.Stamp(parts), Done, moved.Add, CancellationToken.None);

        Assert.Equal((3, 0), counts);
        Assert.Equal(parts, moved);
        Assert.DoesNotContain(neighbour, moved);
        Assert.True(File.Exists(neighbour));
    }

    [Theory]
    [InlineData(ExtractState.Failed, ExtractFailure.Corrupt, 0)]
    [InlineData(ExtractState.NeedPassword, ExtractFailure.None, 0)]
    [InlineData(ExtractState.NeedRarTool, ExtractFailure.None, 0)]
    [InlineData(ExtractState.Done, ExtractFailure.None, 0)]
    [InlineData(ExtractState.Done, ExtractFailure.Io, 1)]
    public void Nothing_is_moved_without_a_full_success(ExtractState state, ExtractFailure failure, int files)
    {
        using var dir = new TempDir();
        var stamps = ArchiveCleanup.Stamp(Files(dir, "a.zip"));
        var moved = new List<string>();

        var result = new ExtractResult(state, failure, Files: files);

        Assert.Equal((0, 0), ArchiveCleanup.Remove(stamps, result, moved.Add, CancellationToken.None));
        Assert.Equal((0, 0), ArchiveCleanup.Remove(stamps, result, moved.Add, CancellationToken.None, allOrNothing: true));
        Assert.Empty(moved);
    }

    [Fact]
    public void Changed_missing_and_linked_files_stay()
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "length", "time", "gone", "link", "good");
        string target = Files(dir, "target")[0];
        File.Delete(parts[3]);
        File.CreateSymbolicLink(parts[3], target);
        var stamps = ArchiveCleanup.Stamp(parts.Append(dir.File("never")));
        DateTime time = File.GetLastWriteTimeUtc(parts[0]);
        File.AppendAllText(parts[0], "x");
        File.SetLastWriteTimeUtc(parts[0], time);
        File.SetLastWriteTimeUtc(parts[1], File.GetLastWriteTimeUtc(parts[1]).AddSeconds(5));
        File.Delete(parts[2]);
        var moved = new List<string>();

        var counts = ArchiveCleanup.Remove(stamps, Done, moved.Add, CancellationToken.None);

        Assert.Equal((1, 5), counts);
        Assert.Equal(new[] { parts[4] }, moved);
    }

    [Fact]
    public void Duplicates_count_once()
    {
        using var dir = new TempDir();
        string part = Files(dir, "a.zip")[0];
        var moved = new List<string>();

        var stamps = ArchiveCleanup.Stamp(new[] { part, part, Path.Combine(dir.Path, ".", "a.zip") });

        Assert.Equal((1, 0), ArchiveCleanup.Remove(stamps, Done, moved.Add, CancellationToken.None));
        Assert.Equal(new[] { part }, moved);
    }

    [Fact]
    public void A_failing_trash_keeps_the_file_and_goes_on()
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "a.7z.001", "a.7z.002");

        var counts = ArchiveCleanup.Remove(ArchiveCleanup.Stamp(parts), Done,
            p => { if (p == parts[0]) throw new IOException("nein"); }, CancellationToken.None);

        Assert.Equal((1, 1), counts);
        Assert.True(File.Exists(parts[0]));
    }

    [Fact]
    public void A_cancelled_run_keeps_the_rest()
    {
        using var dir = new TempDir();
        var stamps = ArchiveCleanup.Stamp(Files(dir, "a.7z.001", "a.7z.002"));
        using var cts = new CancellationTokenSource();
        var moved = new List<string>();

        var counts = ArchiveCleanup.Remove(stamps, Done, p => { moved.Add(p); cts.Cancel(); }, cts.Token);

        Assert.Equal((1, 1), counts);
        Assert.Single(moved);
    }

    [Fact]
    public void All_or_nothing_deletes_every_part_and_nothing_else()
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "a.7z.001", "a.7z.002", "a.7z.003");
        string neighbour = Files(dir, "a.nfo")[0];

        var counts = ArchiveCleanup.Remove(ArchiveCleanup.Stamp(parts), Done, File.Delete, CancellationToken.None, allOrNothing: true);

        Assert.Equal((3, 0), counts);
        Assert.All(parts, p => Assert.False(File.Exists(p)));
        Assert.True(File.Exists(neighbour));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("link")]
    [InlineData("never")]
    public void All_or_nothing_keeps_everything_if_one_part_is_not_untouched(string problem)
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "a.7z.001", "a.7z.002", "a.7z.003");
        string target = Files(dir, "target")[0];
        if (problem == "never") File.Delete(parts[2]);
        var stamps = ArchiveCleanup.Stamp(parts);
        if (problem == "changed") File.AppendAllText(parts[2], "x");
        if (problem == "missing") File.Delete(parts[2]);
        if (problem == "link")
        {
            File.Delete(parts[2]);
            File.CreateSymbolicLink(parts[2], target);
        }
        var deleted = new List<string>();

        var counts = ArchiveCleanup.Remove(stamps, Done, deleted.Add, CancellationToken.None, allOrNothing: true);

        Assert.Equal((0, 3), counts);
        Assert.Empty(deleted);
        Assert.True(File.Exists(parts[0]));
        Assert.True(File.Exists(parts[1]));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void All_or_nothing_does_not_start_when_already_cancelled()
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "a.7z.001", "a.7z.002");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var counts = ArchiveCleanup.Remove(ArchiveCleanup.Stamp(parts), Done, File.Delete, cts.Token, allOrNothing: true);

        Assert.Equal((0, 2), counts);
        Assert.All(parts, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void All_or_nothing_finishes_when_cancelled_while_deleting()
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "a.7z.001", "a.7z.002", "a.7z.003");
        using var cts = new CancellationTokenSource();

        var counts = ArchiveCleanup.Remove(ArchiveCleanup.Stamp(parts), Done, p => { File.Delete(p); cts.Cancel(); }, cts.Token,
            allOrNothing: true);

        Assert.Equal((3, 0), counts);
        Assert.All(parts, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void All_or_nothing_goes_on_after_a_file_that_cannot_be_deleted()
    {
        using var dir = new TempDir();
        string[] parts = Files(dir, "a.7z.001", "a.7z.002", "a.7z.003");

        var counts = ArchiveCleanup.Remove(ArchiveCleanup.Stamp(parts), Done, p =>
        {
            if (p == parts[1]) throw new UnauthorizedAccessException("nein");
            File.Delete(p);
        }, CancellationToken.None, allOrNothing: true);

        Assert.Equal((2, 1), counts);
        Assert.Equal(new[] { false, true, false }, parts.Select(File.Exists));
    }

    /// <summary>Entpackt das Archiv in den eigenen Ordner, muss der Eintrag ausweichen: Gelöscht wird das Archiv, nie das Entpackte.</summary>
    [Fact]
    public void An_extracted_file_with_the_name_of_the_archive_stays()
    {
        using var dir = new TempDir();
        string archive = dir.File("a.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("a.zip").Open());
            writer.Write("hallo");
        }
        var stamps = ArchiveCleanup.Stamp(new[] { archive });
        var result = new ArchiveExtractor(() => null, () => null, _ => long.MaxValue)
            .Run(new ExtractJob(archive, dir.Path, ArchiveType.Zip, false, new[] { archive }), CancellationToken.None);

        var counts = ArchiveCleanup.Remove(stamps, result, File.Delete, CancellationToken.None, allOrNothing: true);

        Assert.Equal((1, 0), counts);
        Assert.False(File.Exists(archive));
        Assert.Equal("hallo", File.ReadAllText(dir.File("a (1).zip")));
    }
}
