using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using D6Downloader.Packages;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Der eingebaute ZIP-Weg meldet nur dann Erfolg, wenn Länge und Prüfsumme jedes Eintrags stimmen.</summary>
public class ZipIntegrityTests
{
    private static ExtractResult Run(TempDir dir, string archive, string? tar = null) =>
        new ArchiveExtractor(() => null, () => tar, _ => long.MaxValue)
            .Run(new ExtractJob(archive, dir.File("out"), ArchiveType.Zip, false, new[] { archive }), CancellationToken.None);

    private static string Zip(TempDir dir, CompressionLevel level, params (string Name, byte[] Data)[] entries)
    {
        string archive = dir.File("a.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var s = zip.CreateEntry(name, level).Open();
            s.Write(data);
        }
        return archive;
    }

    private static byte[] Random(int length)
    {
        var data = new byte[length];
        new Random(3).NextBytes(data);
        return data;
    }

    [Fact]
    public void The_checksum_is_the_usual_crc32()
    {
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Append(0, data, data.Length));
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void A_flipped_byte_is_corrupt_and_nothing_is_taken_over(CompressionLevel level)
    {
        using var dir = new TempDir();
        string archive = Zip(dir, level, ("gut.txt", new byte[] { 1, 2, 3 }), ("x.bin", Random(4000)));
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[2000] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
        // Auch mit bsdtar in der Kette bleibt es dabei.
        string? tar = File.Exists("/usr/bin/bsdtar") ? "/usr/bin/bsdtar" : null;
        var stamps = ArchiveCleanup.Stamp(new[] { archive });

        var result = Run(dir, archive, tar);

        Assert.Equal((ExtractState.Failed, ExtractFailure.Corrupt), (result.State, result.Failure));
        Assert.False(Directory.Exists(dir.File("out")));
        Assert.Equal((0, 0), ArchiveCleanup.Remove(stamps, result, File.Delete, CancellationToken.None, allOrNothing: true));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void A_folder_entry_with_data_is_corrupt()
    {
        using var dir = new TempDir();
        string archive = Zip(dir, CompressionLevel.NoCompression, ("ordner/", new byte[] { 1, 2, 3 }), ("a.txt", new byte[] { 1 }));

        var result = Run(dir, archive);

        Assert.Equal((ExtractState.Failed, ExtractFailure.Corrupt), (result.State, result.Failure));
        Assert.False(Directory.Exists(dir.File("out")));
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void Valid_archives_with_folders_empty_and_large_files_are_done(CompressionLevel level)
    {
        using var dir = new TempDir();
        byte[] large = Random(5_000_000);
        string archive = Zip(dir, level, ("ordner/", Array.Empty<byte>()), ("leer.txt", Array.Empty<byte>()), ("ordner/gross.bin", large));

        var result = Run(dir, archive);

        Assert.Equal((ExtractState.Done, 2, false), (result.State, result.Files, result.Unverified));
        Assert.Empty(File.ReadAllBytes(Path.Combine(dir.File("out"), "leer.txt")));
        Assert.Equal(large, File.ReadAllBytes(Path.Combine(dir.File("out"), "ordner", "gross.bin")));
    }

    /// <summary>Von Hand gebaut: Größen und Verzeichnislage stehen nur in den ZIP64-Feldern.</summary>
    [Fact]
    public void A_valid_zip64_is_done()
    {
        using var dir = new TempDir();
        string archive = dir.File("a.zip");
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        byte[] name = Encoding.ASCII.GetBytes("a.txt");
        const uint Max = 0xFFFFFFFF;
        using (var w = new BinaryWriter(File.Create(archive)))
        {
            w.Write(0x04034b50u); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u);
            w.Write(0xCBF43926u); w.Write(Max); w.Write(Max); w.Write((ushort)name.Length); w.Write((ushort)20);
            w.Write(name); w.Write((ushort)1); w.Write((ushort)16); w.Write((ulong)data.Length); w.Write((ulong)data.Length);
            w.Write(data);
            long central = w.BaseStream.Position;
            w.Write(0x02014b50u); w.Write((ushort)45); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u);
            w.Write(0xCBF43926u); w.Write(Max); w.Write(Max); w.Write((ushort)name.Length); w.Write((ushort)28);
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(Max);
            w.Write(name); w.Write((ushort)1); w.Write((ushort)24); w.Write((ulong)data.Length); w.Write((ulong)data.Length); w.Write(0UL);
            long end = w.BaseStream.Position;
            w.Write(0x06064b50u); w.Write(44UL); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
            w.Write(1UL); w.Write(1UL); w.Write((ulong)(end - central)); w.Write((ulong)central);
            w.Write(0x07064b50u); w.Write(0u); w.Write((ulong)end); w.Write(1u);
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
            w.Write(Max); w.Write(Max); w.Write((ushort)0);
        }

        var result = Run(dir, archive);

        Assert.Equal(ExtractState.Done, result.State);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(dir.File("out"), "a.txt")));
    }

    [Fact]
    public void An_unverified_result_keeps_every_part()
    {
        using var dir = new TempDir();
        string archive = dir.File("a.rar");
        File.WriteAllText(archive, "x");
        var result = new ExtractResult(ExtractState.Done, Files: 1, Unverified: true);

        foreach (bool allOrNothing in new[] { false, true })
            Assert.Equal((0, 1), ArchiveCleanup.Remove(ArchiveCleanup.Stamp(new[] { archive }), result, File.Delete,
                CancellationToken.None, allOrNothing));
        Assert.True(File.Exists(archive));
    }
}
