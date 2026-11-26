using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using D6Downloader.Packages;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Nur ein belegt vollständiges RAR darf entfernt werden. Die Entpack-Tests brauchen <c>unar</c>, sonst prüfen sie nichts.</summary>
public class RarCompletenessTests
{
    private static readonly (string, byte[])[] Three =
    {
        ("eins.txt", Encoding.ASCII.GetBytes("erste Datei")),
        ("zwei.txt", Encoding.ASCII.GetBytes(new string('z', 500))),
        ("drei.txt", Encoding.ASCII.GetBytes("dritte")),
    };

    private static string Write(TempDir dir, byte[] bytes, string name = "a.rar")
    {
        string path = dir.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static bool Proven(params string[] parts) => RarCompleteness.IsProven(parts);

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void A_whole_archive_is_proven_and_a_cut_one_is_not(int version)
    {
        using var dir = new TempDir();
        byte[] whole = RarSamples.Several(version, Three, out var ends);

        Assert.True(Proven(Write(dir, whole)));
        Assert.True(Proven(Write(dir, whole.Concat(new byte[100]).ToArray()))); // aufgefüllt mit Nullen
        foreach (int end in ends) Assert.False(Proven(Write(dir, whole[..end])));
        for (int length = 0; length < whole.Length; length++) Assert.False(Proven(Write(dir, whole[..length])), "Länge " + length);
        Assert.False(Proven(Write(dir, whole.Concat(new byte[] { 1, 2, 3 }).ToArray())));
        Assert.False(Proven(Write(dir, RarSamples.Several(version, Three, out _, endBlock: false))));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void A_damaged_header_is_not_proven(int version)
    {
        using var dir = new TempDir();
        byte[] whole = RarSamples.Several(version, Three, out var ends);
        // Alles außer den Inhalten ist Kopf: Jedes gekippte Byte dort muss auffallen.
        var content = new HashSet<int>();
        for (int i = 0; i < Three.Length; i++)
            for (int k = ends[i] - Three[i].Item2.Length; k < ends[i]; k++) content.Add(k);

        for (int at = 0; at < whole.Length; at++)
        {
            if (content.Contains(at)) continue;
            var bytes = (byte[])whole.Clone();
            bytes[at] ^= 0xFF;
            Assert.False(Proven(Write(dir, bytes)), "Byte " + at);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void A_self_extracting_archive_is_proven_only_behind_a_program(int version)
    {
        using var dir = new TempDir();
        byte[] whole = RarSamples.Several(version, Three, out _);
        byte[] stub = Encoding.ASCII.GetBytes("MZ stub Rar!\u001a\u0007 nur Text im Programm ").Concat(new byte[3000]).ToArray();

        Assert.True(Proven(Write(dir, stub.Concat(whole).ToArray(), "a.exe")));
        Assert.False(Proven(Write(dir, new byte[100].Concat(whole).ToArray())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_set_needs_every_volume_and_the_last_one(bool oldNames)
    {
        using var dir = new TempDir();
        string[] parts = RarSamples.Stored(dir.Path, "a.bin", new byte[3000], volumes: 3, oldNames: oldNames);

        Assert.True(Proven(parts));
        Assert.True(Proven(parts.Reverse().ToArray())); // die Reihenfolge der Download-Liste ist gleich
        Assert.False(Proven(parts[0], parts[1]));
        Assert.False(Proven(parts[1], parts[2]));
        Assert.False(Proven(parts[0]));
        File.WriteAllBytes(parts[2], File.ReadAllBytes(parts[2])[..^7]); // ohne Ende-Block
        Assert.False(Proven(parts));
    }

    /// <summary>Echte Archive aus libarchive (mit WinRAR erzeugt).</summary>
    [Theory]
    [InlineData("rar", true)]
    [InlineData("rar5_stored", true)]
    [InlineData("rar_encryption_header", false)]
    [InlineData("rar5_encrypted_filenames", false)]
    public void Real_archives(string fixture, bool proven)
    {
        using var dir = new TempDir();
        Assert.Equal(proven, Proven(RarSamples.Fixture(fixture, dir.Path)));
    }

    [Theory]
    [InlineData("rar5_multiarchive.part{0:D2}", 8)]
    [InlineData("rar_multivolume.part{0:D4}", 4)]
    public void Real_volume_sets(string format, int count)
    {
        using var dir = new TempDir();
        string[] parts = RarSamples.Volumes(dir.Path, format, Enumerable.Range(1, count));

        Assert.True(Proven(parts));
        Assert.False(Proven(parts[..^1]));
    }

    private static ArchiveExtractor? Unar()
    {
        var env = ToolSearchEnv.Current(Array.Empty<string>());
        if (ToolLocator.FindUnar(env) is not { } unar || ToolLocator.FindLsar(env) is not { } lsar) return null;
        return new ArchiveExtractor(() => null, () => null, _ => long.MaxValue, findUnar: () => unar, findLsar: () => lsar);
    }

    [Theory]
    [InlineData(4, -1, true)]
    [InlineData(4, 0, true)]
    [InlineData(4, 1, true)]
    [InlineData(5, -1, true)]
    [InlineData(5, 0, true)]
    [InlineData(5, 1, true)]
    [InlineData(4, -1, false)]
    [InlineData(5, -1, false)]
    public void Only_a_proven_archive_is_removed_after_extracting(int version, int cut, bool endBlock)
    {
        if (Unar() is not { } extractor) return;
        using var dir = new TempDir();
        byte[] bytes = RarSamples.Several(version, Three, out var ends, endBlock);
        string archive = Write(dir, cut < 0 ? bytes : bytes[..ends[cut]]); // genau zwischen zwei Dateien
        bool whole = cut < 0 && endBlock;

        foreach (bool allOrNothing in new[] { false, true })
        {
            var stamps = ArchiveCleanup.Stamp(new[] { archive });
            var removed = new List<string>();
            var result = extractor.Run(new ExtractJob(archive, dir.File("out" + allOrNothing), ArchiveType.Rar, false, new[] { archive }),
                CancellationToken.None);

            // Kein Fehler: unar meldet auch das abgeschnittene als entpackt.
            Assert.Equal((ExtractState.Done, cut < 0 ? 3 : cut + 1, !whole), (result.State, result.Files, result.Unverified));
            Assert.Equal(whole ? (1, 0) : (0, 1), ArchiveCleanup.Remove(stamps, result, removed.Add, CancellationToken.None, allOrNothing));
            Assert.Equal(whole ? 1 : 0, removed.Count);
        }
    }
}
