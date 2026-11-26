using System;
using System.Collections.Generic;
using System.IO;
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

        var counts = ArchiveCleanup.Remove(stamps, new ExtractResult(state, failure, Files: files), moved.Add, CancellationToken.None);

        Assert.Equal((0, 0), counts);
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
}
