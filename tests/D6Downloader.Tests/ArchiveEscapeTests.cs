using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using D6Downloader.Packages;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>
/// Selbst erzeugte Archive, die über Verknüpfungen aus dem Zielordner hinaus schreiben wollen, gegen das echte 7-Zip und
/// bsdtar. Alles bleibt in einem Ordner unter /tmp, auch das "Außerhalb". Fehlt ein Werkzeug, prüft sein Fall nichts.
/// </summary>
public class ArchiveEscapeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _outside, _target;

    private static readonly string? SevenZip = ToolLocator.FindSevenZip(null, ToolSearchEnv.Current(Array.Empty<string>()));
    private static readonly string? BsdTar = ToolLocator.FindTar(ToolSearchEnv.Current(Array.Empty<string>()));

    public ArchiveEscapeTests()
    {
        _outside = _dir.File("outside");
        _target = _dir.File("pkg/ziel");
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_outside, "victim"), "orig");
    }

    public void Dispose() => _dir.Dispose();

    private sealed record Item(string Name, char Kind, string Value = "");

    private static Item F(string name, string content = "x") => new(name, 'f', content);
    private static Item S(string name, string target) => new(name, 's', target);
    private static Item H(string name, string target) => new(name, 'h', target);

    /// <summary>"$O" steht für den Ordner außerhalb, "$R" für denselben Ordner als relativen Pfad.</summary>
    private List<Item> Case(string name)
    {
        string o = _outside, r = string.Concat(Enumerable.Repeat("../", 12)) + _outside.TrimStart('/');
        return name switch
        {
            "absolute-link" => new() { S("l", o), F("l/pwn") },
            "relative-link" => new() { S("l", r), F("l/pwn") },
            "file-over-link" => new() { S("v", o + "/victim"), F("v", "changed") },
            "link-chain" => new() { S("d", "."), S("e", "d/../../../outside"), F("e/pwn") },
            "link-chain-up" => new() { F("d1/d2/x"), S("s1", "d1/d2"), S("s2", "s1/../../../../.."), F("s2/pwn") },
            "only-a-link" => new() { S("l", o) },
            "hardlink" => new() { H("h", o + "/victim"), F("h", "changed") },
            "hardlink-via-link" => new() { S("l", o), H("h", "l/victim"), F("h", "changed") },
            "dotdot" => new() { F("../../outside/pwn") },
            _ => throw new ArgumentException(name),
        };
    }

    private static void WriteTar(string path, IEnumerable<Item> items)
    {
        using var writer = new TarWriter(File.Create(path), TarEntryFormat.Pax);
        foreach (var item in items)
        {
            var type = item.Kind switch { 's' => TarEntryType.SymbolicLink, 'h' => TarEntryType.HardLink, _ => TarEntryType.RegularFile };
            var entry = new PaxTarEntry(type, item.Name);
            if (item.Kind == 'f') entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(item.Value));
            else entry.LinkName = item.Value;
            writer.WriteEntry(entry);
        }
    }

    private static void WriteZip(string path, IEnumerable<Item> items)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in items)
        {
            var entry = zip.CreateEntry(item.Name);
            // Unix-Typ in den oberen 16 Bit: Verknüpfung (0120777) oder Datei (0100644).
            entry.ExternalAttributes = (item.Kind == 's' ? 0xA1FF : 0x81A4) << 16;
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(item.Value));
        }
    }

    private ExtractResult? Extract(bool sevenZip, string archive)
    {
        if ((sevenZip ? SevenZip : BsdTar) is not { } tool) return null;
        var extractor = new ArchiveExtractor(() => sevenZip ? tool : null, () => sevenZip ? null : tool, _ => long.MaxValue);
        // Als 7z angemeldet, damit nicht .NET entpackt. Die Werkzeuge erkennen das Format am Inhalt.
        return extractor.Run(new ExtractJob(archive, _target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
    }

    private void AssertContained()
    {
        Assert.Equal("orig", File.ReadAllText(Path.Combine(_outside, "victim")));
        Assert.Equal(new[] { Path.Combine(_outside, "victim") }, Directory.GetFileSystemEntries(_outside));
        Assert.Equal(new[] { "archive", "outside", "pkg" },
            Directory.GetFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p).Split('.')[0]).Order());
        if (!Directory.Exists(_target)) return;
        Assert.Equal(new[] { _target }, Directory.GetFileSystemEntries(_dir.File("pkg")));
        foreach (var info in new DirectoryInfo(_target).EnumerateFileSystemInfos("*",
                     new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            Assert.Null(info.LinkTarget);
        Assert.Empty(Directory.GetDirectories(_target, ".ydl-extract-*"));
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string name in new[]
        {
            "absolute-link", "relative-link", "file-over-link", "link-chain", "link-chain-up", "only-a-link",
            "hardlink", "hardlink-via-link", "dotdot",
        })
        foreach (bool sevenZip in new[] { true, false })
        foreach (bool zip in new[] { true, false })
        {
            if (zip && name.StartsWith("hardlink", StringComparison.Ordinal)) continue; // ZIP kennt keine harten Links
            yield return new object[] { name, sevenZip, zip };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Nothing_is_written_outside_the_target(string name, bool sevenZip, bool zip)
    {
        string archive = _dir.File("archive.rar");
        if (zip) WriteZip(archive, Case(name));
        else WriteTar(archive, Case(name));

        Extract(sevenZip, archive);

        AssertContained();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_archive_with_a_link_leading_outside_is_rejected(bool sevenZip)
    {
        string archive = _dir.File("archive.rar");
        WriteTar(archive, new[] { F("a.txt"), S("l", _outside) });

        if (Extract(sevenZip, archive) is not { } result) return;

        Assert.Equal(ExtractState.Failed, result.State);
        // bsdtar listet "l -> Ziel": Das lehnt schon die Pfadprüfung vor dem Entpacken ab.
        Assert.Equal(sevenZip ? ExtractFailure.Link : ExtractFailure.UnsafePath, result.Failure);
        AssertContained();
    }

    /// <summary>Ordner ohne Rechte dürfen weder das Entpacken noch das Aufräumen blockieren.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Folders_without_permissions_leave_no_staging_behind(bool sevenZip)
    {
        string archive = _dir.File("archive.rar");
        using (var writer = new TarWriter(File.Create(archive), TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "zu/datei") { DataStream = new MemoryStream(new byte[] { 1 }) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "zu") { Mode = UnixFileMode.None });
        }

        if (Extract(sevenZip, archive) is not { } result) return;

        Assert.Equal(ExtractState.Done, result.State);
        Assert.True(File.Exists(Path.Combine(_target, "zu", "datei")));
        AssertContained();
    }
}
