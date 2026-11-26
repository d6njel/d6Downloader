using System;
using System.IO;
using System.Linq;
using System.Threading;
using D6Downloader.Packages;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>
/// RAR mit dem echten <c>unar</c>/<c>lsar</c> (und dem echten 7-Zip, soweit es darum geht, dass es hier nicht gefragt ist).
/// Fehlt <c>unar</c>, tun die Tests nichts: Dann bleibt der RAR-Weg ungeprüft. Entschlüsseln mit richtigem Passwort ist
/// ungeprüft, die Passwörter der libarchive-Dateien sind nicht bekannt.
/// </summary>
public class RarExtractTests : IDisposable
{
    private static readonly ToolSearchEnv Env = ToolSearchEnv.Current(Array.Empty<string>());
    private static readonly string? Unar = ToolLocator.FindUnar(Env);
    private static readonly string? Lsar = ToolLocator.FindLsar(Env);
    private static readonly string? SevenZip = ToolLocator.FindSevenZip(null, Env);
    private static bool Missing => Unar is null || Lsar is null;

    private readonly TempDir _dir = new();
    private readonly string _in, _out;

    public RarExtractTests()
    {
        _in = _dir.File("in");
        _out = _dir.File("out");
        Directory.CreateDirectory(_in);
    }

    public void Dispose() => _dir.Dispose();

    private ExtractResult Run(string[] parts, bool allowPassword = false, string[]? passwords = null) =>
        new ArchiveExtractor(() => SevenZip, () => null, _ => long.MaxValue, TimeSpan.FromMinutes(2), () => Unar, () => Lsar, allowPassword)
            .Run(new ExtractJob(parts[0], _out, ArchiveType.Rar, parts.Length > 1, parts, passwords), CancellationToken.None);

    private void AssertNothingExtracted() => Assert.False(Directory.Exists(_out), "Ziel sollte nicht angelegt bleiben");

    private string[] Rar5Volumes(params int[] numbers) => RarSamples.Volumes(_in, "rar5_multiarchive.part{0:D2}", numbers);

    private static byte[] Content(int length = 9000)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    private string[] Files() => Directory.GetFiles(_out, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(_out, f)).Order().ToArray();

    [Fact]
    public void Single_rar5_is_extracted()
    {
        if (Missing) return;
        string archive = RarSamples.Fixture("rar5_stored", _in);
        var r = Run(new[] { archive });
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(1, r.Files);
        Assert.Equal(29, new FileInfo(Path.Combine(_out, "helloworld.txt")).Length);
        Assert.True(File.Exists(archive));
        Assert.Empty(Directory.GetDirectories(_out, ".ydl-extract-*"));
    }

    [Fact]
    public void Multipart_rar5_is_extracted()
    {
        if (Missing) return;
        var parts = Rar5Volumes(Enumerable.Range(1, 8).ToArray());
        var r = Run(parts);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(2, r.Files);
        const string bin = "home/antek/temp/build/unrar5/libarchive/bin/";
        Assert.Equal(144608, new FileInfo(Path.Combine(_out, bin + "bsdcat_test")).Length);
        Assert.Equal(365672, new FileInfo(Path.Combine(_out, bin + "bsdtar_test")).Length);
        Assert.All(parts, p => Assert.True(File.Exists(p)));
    }

    [Theory]
    [InlineData(false)] // set.part1.rar …
    [InlineData(true)]  // set.rar, set.r00 …
    public void Multipart_rar4_is_extracted(bool oldNames)
    {
        if (Missing) return;
        byte[] content = Content();
        var parts = RarSamples.Stored(_in, "ordner\\daten.bin", content, 3, oldNames);
        var r = Run(parts);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_out, "ordner", "daten.bin")));
    }

    /// <summary>Das mehrteilige RAR 4 aus libarchive enthält eine Verknüpfung: Es wird als Ganzes abgelehnt.</summary>
    [Fact]
    public void Libarchive_multipart_rar4_with_a_link_is_rejected()
    {
        if (Missing) return;
        var parts = RarSamples.Volumes(_in, "rar_multivolume.part{0:D4}", Enumerable.Range(1, 4));
        var r = Run(parts);
        Assert.Equal(ExtractFailure.Link, r.Failure);
        AssertNothingExtracted();
    }

    [Theory]
    [InlineData("rar")]
    [InlineData("rar5_symlink")]
    [InlineData("rar5_hardlink")]
    public void Links_are_rejected(string fixture)
    {
        if (Missing) return;
        var r = Run(new[] { RarSamples.Fixture(fixture, _in) });
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.Link, r.Failure);
        AssertNothingExtracted();
    }

    [Theory]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 1, 2, 3 })]              // das Ende fehlt: an den Namen nicht zu sehen
    [InlineData(new[] { 1, 2, 4, 5, 6, 7, 8 })]  // Lücke
    public void Missing_rar5_volumes_are_reported_as_missing(int[] present)
    {
        if (Missing) return;
        var parts = Rar5Volumes(present);
        // Ein Paket mit nur einem Teil gibt es nicht, der Entpacker bekommt es trotzdem als mehrteilig.
        var job = new ExtractJob(parts[0], _out, ArchiveType.Rar, true, parts);
        var r = new ArchiveExtractor(() => null, () => null, _ => long.MaxValue, findUnar: () => Unar, findLsar: () => Lsar)
            .Run(job, CancellationToken.None);
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.MissingParts, r.Failure);
        AssertNothingExtracted();
    }

    [Theory]
    [InlineData(1)] // Lücke in der Mitte
    [InlineData(2)] // letzter Teil fehlt
    public void Missing_rar4_volume_is_reported_as_missing(int gone)
    {
        if (Missing) return;
        var parts = RarSamples.Stored(_in, "daten.bin", Content(), 3);
        var present = parts.Where((_, i) => i != gone).ToArray();
        File.Delete(parts[gone]);
        var r = Run(present);
        Assert.Equal(ExtractFailure.MissingParts, r.Failure);
        AssertNothingExtracted();
    }

    [Theory]
    [InlineData("daten.bin")]
    [InlineData("wrong password.txt")]                 // Dateinamen entscheiden nie über die Ursache
    [InlineData("This archive requires a password.txt")]
    [InlineData("a... Failed! (Missing or wrong password)")]
    public void Damaged_content_is_corrupt_not_a_password_problem(string name)
    {
        if (Missing) return;
        string archive = RarSamples.Stored(_in, name, Content())[0];
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
        var r = Run(new[] { archive });
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.Corrupt, r.Failure);
        AssertNothingExtracted();
    }

    [Fact]
    public void Truncated_and_garbage_files_are_corrupt()
    {
        if (Missing) return;
        string archive = RarSamples.Stored(_in, "daten.bin", Content())[0];
        File.WriteAllBytes(archive, File.ReadAllBytes(archive)[..4000]);
        Assert.Equal(ExtractFailure.Corrupt, Run(new[] { archive }).Failure);
        File.WriteAllText(archive, "kein Archiv");
        Assert.Equal(ExtractFailure.Corrupt, Run(new[] { archive }).Failure);
        AssertNothingExtracted();
    }

    [Theory]
    [InlineData("..\\raus.txt")]                   // RAR 4 trennt mit "\"
    [InlineData("a\\..\\..\\raus.txt")]
    [InlineData("../raus.txt")]
    [InlineData("/tmp/raus.txt")]
    public void Names_leading_outside_are_rejected(string name)
    {
        if (Missing) return;
        var r = Run(new[] { RarSamples.Stored(_in, name, Content(100))[0] });
        // unar/lsar räumen manche Namen selbst auf; abgelehnt oder sicher im Ziel, nie außerhalb.
        if (r.State == ExtractState.Done) Assert.All(Files(), f => Assert.DoesNotContain("..", f));
        else Assert.Equal(ExtractFailure.UnsafePath, r.Failure);
        Assert.False(File.Exists(_dir.File("raus.txt")));
        Assert.Equal(new[] { "in" }, Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).Where(n => n != "out").ToArray());
    }

    public static TheoryData<string> Encrypted => new()
    {
        "rar4_encrypted", "rar5_encrypted", "rar_encryption_header", "rar5_encrypted_filenames",
    };

    [Theory]
    [MemberData(nameof(Encrypted))]
    public void Encrypted_rar_without_permission_says_so(string fixture)
    {
        if (Missing) return;
        var r = Run(new[] { RarSamples.Fixture(fixture, _in) }, passwords: new[] { "Geheim123" });
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.PasswordNeedsTool, r.Failure);
        Assert.Null(r.Detail);
        AssertNothingExtracted();
    }

    [Theory]
    [MemberData(nameof(Encrypted))]
    public void Encrypted_rar_with_wrong_password_asks_for_one(string fixture)
    {
        if (Missing) return;
        string archive = RarSamples.Fixture(fixture, _in);

        var none = Run(new[] { archive }, allowPassword: true);
        Assert.Equal(ExtractState.NeedPassword, none.State);
        Assert.Null(none.Detail);

        var wrong = Run(new[] { archive }, allowPassword: true, passwords: new[] { "falsch-1", "-falsch 2" });
        Assert.Equal(ExtractState.NeedPassword, wrong.State);
        Assert.Equal("2", wrong.Detail);
        Assert.Null(wrong.UsedPassword);
        AssertNothingExtracted();
    }

    [Fact]
    public void This_7zip_is_judged_by_its_own_format_list()
    {
        if (SevenZip is null) return;
        var parts = RarSamples.Stored(_in, "daten.bin", Content(), 3);
        var r = new ArchiveExtractor(() => SevenZip, () => null, _ => long.MaxValue)
            .Run(new ExtractJob(parts[0], _out, ArchiveType.Rar, true, parts), CancellationToken.None);
        // Fedoras 7-Zip kann kein RAR: „unar nötig“, nie „beschädigt“. Ein 7-Zip mit RAR entpackt.
        Assert.Contains(r.State, new[] { ExtractState.NeedRarTool, ExtractState.Done });
    }
}
