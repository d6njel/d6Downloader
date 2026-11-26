using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using D6Downloader.Downloads;
using D6Downloader.Packages;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>
/// Der RAR-Weg gegen Attrappen von <c>lsar</c>, <c>unar</c>, <c>7z</c> und <c>bsdtar</c> (Shellskripte, die ihre Aufrufe
/// mitschreiben): Was wird wann gestartet, mit welchen Argumenten, und was wird aus den Meldungen.
/// </summary>
public class UnarFakeTests : IDisposable
{
    private const string Secret = "Streng geheim";
    private const string Ask = "This archive requires a password to unpack. Use the -p option to provide one.";

    private readonly TempDir _dir = new();
    private readonly string _log, _out, _archive;

    public UnarFakeTests()
    {
        _log = _dir.File("aufrufe.log");
        _out = _dir.File("out");
        _archive = _dir.File("set.part1.rar");
        File.WriteAllText(_archive, "x");
    }

    public void Dispose() => _dir.Dispose();

    private string Script(string name, string body)
    {
        string path = _dir.File(name);
        File.WriteAllText(path, $"#!/bin/sh\necho \"{name} $*\" >> '{_log}'\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static object Entry(string name, long size = 5, Dictionary<string, object>? extra = null)
    {
        var e = new Dictionary<string, object> { ["XADFileName"] = name, ["XADFileSize"] = size };
        foreach (var pair in extra ?? new()) e[pair.Key] = pair.Value;
        return e;
    }

    private static string Json(object[] entries, bool encrypted = false, int? error = null)
    {
        var root = new Dictionary<string, object>
        {
            ["lsarFormatVersion"] = 2,
            ["lsarContents"] = entries,
            ["lsarProperties"] = encrypted ? new Dictionary<string, object> { ["XADIsEncrypted"] = 1 } : new Dictionary<string, object>(),
        };
        if (error is { } code) root["lsarError"] = code;
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private string Lsar(string json, string before = "")
    {
        File.WriteAllText(_dir.File("listing.json"), json);
        return Script("lsar", before + $"\ncat '{_dir.File("listing.json")}'");
    }

    /// <summary>Im Skript steht <c>$out</c> für den Zielordner aus <c>-o</c> und <c>$pw</c> für das Passwort aus <c>-p</c>.</summary>
    private string Unar(string body) => Script("unar",
        "out=; pw=\nwhile [ $# -gt 1 ]; do\n  case \"$1\" in -o) out=$2; shift;; -p) pw=$2; shift;; -k) shift;; esac\n  shift\ndone\n" + body);

    private const string WriteA = "printf hallo > \"$out/a.txt\"";

    private ArchiveExtractor Extractor(string? lsar, string? unar, bool allow = false, string? sevenZip = null, string? tar = null,
        TimeSpan? timeout = null, long free = long.MaxValue) =>
        new(() => sevenZip, () => tar, _ => free, timeout, () => unar, () => lsar, allow);

    private ExtractJob Job(bool multi = false, string[]? passwords = null, string[]? parts = null) =>
        new(_archive, _out, ArchiveType.Rar, multi, parts ?? new[] { _archive }, passwords);

    private string[] Calls(string tool) =>
        File.Exists(_log) ? File.ReadAllLines(_log).Where(l => l.StartsWith(tool + " ", StringComparison.Ordinal)).ToArray() : Array.Empty<string>();

    [Fact]
    public void Success_lists_first_and_extracts_into_staging()
    {
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt") })), Unar(WriteA)).Run(Job(), CancellationToken.None);

        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(1, r.Files);
        Assert.Equal("hallo", File.ReadAllText(Path.Combine(_out, "a.txt")));
        Assert.Equal($"lsar -j -jss -nr {_archive}", Assert.Single(Calls("lsar")));
        string call = Assert.Single(Calls("unar"));
        Assert.StartsWith($"unar -o {_out}/.ydl-extract-", call);
        Assert.EndsWith($" -D -nr -r -k skip {_archive}", call);
        Assert.StartsWith("lsar ", File.ReadAllLines(_log)[0]);
        Assert.Empty(Directory.GetDirectories(_out, ".ydl-extract-*"));
    }

    [Fact]
    public void Multipart_hands_over_only_the_first_part()
    {
        string second = _dir.File("set.part2.rar");
        File.WriteAllText(second, "y");
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt") })), Unar(WriteA))
            .Run(Job(multi: true, parts: new[] { _archive, second }), CancellationToken.None);

        Assert.Equal(ExtractState.Done, r.State);
        Assert.DoesNotContain("part2", File.ReadAllText(_log));
    }

    [Theory]
    [InlineData("../raus.txt")]
    [InlineData("a/../../raus.txt")]
    [InlineData("/etc/raus")]
    [InlineData("zeile\nzwei")]
    public void Unsafe_names_stop_before_unar_runs(string name)
    {
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt"), Entry(name) })), Unar(WriteA)).Run(Job(), CancellationToken.None);

        Assert.Equal(ExtractFailure.UnsafePath, r.Failure);
        Assert.Empty(Calls("unar"));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("XADIsLink", 1)]
    [InlineData("XADIsHardLink", 1)]
    [InlineData("XADLinkDestination", "/etc")]
    [InlineData("RAR5RedirectionType", 5)]
    [InlineData("XADIsFIFO", 1)]
    [InlineData("XADPosixPermissions", 0xA1FF)]
    public void Links_in_the_listing_stop_before_unar_runs(string key, object value)
    {
        var link = Entry("l", extra: new() { [key] = value });
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt"), link })), Unar(WriteA)).Run(Job(), CancellationToken.None);

        Assert.Equal(ExtractFailure.Link, r.Failure);
        Assert.Empty(Calls("unar"));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void A_symlink_that_shows_up_in_staging_is_refused()
    {
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt") })), Unar(WriteA + "\nln -s /etc \"$out/l\"")).Run(Job(), CancellationToken.None);

        Assert.Equal(ExtractFailure.Link, r.Failure);
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_permission_no_password_reaches_the_command_line(bool encryptedListing)
    {
        // Verschlüsselte Inhalte (Verzeichnis lesbar) und verschlüsseltes Verzeichnis.
        string lsar = encryptedListing
            ? Lsar(Json(new[] { Entry("a.txt") }, encrypted: true))
            : Script("lsar", $"echo '{Ask}' >&2\nexit 2");
        var r = Extractor(lsar, Unar(WriteA)).Run(Job(passwords: new[] { Secret, "zwei" }), CancellationToken.None);

        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.PasswordNeedsTool, r.Failure);
        Assert.Null(r.Detail);
        Assert.Single(Calls("lsar"));
        Assert.Empty(Calls("unar"));
        string log = File.ReadAllText(_log);
        Assert.DoesNotContain(" -p ", log);
        Assert.DoesNotContain(Secret, log);
    }

    [Fact]
    public void With_permission_passwords_are_tried_until_one_fits()
    {
        string lsar = Lsar(Json(new[] { Entry("a.txt") }, encrypted: true));
        string unar = Unar($"if [ \"$pw\" = '{Secret}' ]; then {WriteA}; exit 0; fi\necho '  a.txt  (5 B)... Failed! (Missing or wrong password)'\nexit 1");
        var r = Extractor(lsar, unar, allow: true).Run(Job(passwords: new[] { "falsch", Secret }), CancellationToken.None);

        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(Secret, r.UsedPassword);
        Assert.DoesNotContain(" -p ", Calls("lsar")[0]); // zuerst immer ohne
        Assert.Equal(2, Calls("unar").Length);
    }

    [Theory]
    [InlineData("Missing or wrong password")]
    [InlineData("Unknown error")]                                    // RAR 4 mit falschem Passwort
    [InlineData("Attempted to read more data than was available")]
    public void Wrong_password_asks_again_and_never_repeats_it(string reason)
    {
        string lsar = Lsar(Json(new[] { Entry("a.txt") }, encrypted: true));
        string unar = Unar($"echo \"  a.txt  (5 B)... Failed! ({reason})\"\necho \"tried $pw\"\nexit 1");
        var r = Extractor(lsar, unar, allow: true).Run(Job(passwords: new[] { Secret }), CancellationToken.None);

        Assert.Equal(ExtractState.NeedPassword, r.State);
        Assert.Equal("1", r.Detail);
        Assert.Null(r.UsedPassword);
    }

    [Fact]
    public void A_password_echoed_in_an_unknown_error_stays_out_of_the_detail()
    {
        string lsar = Lsar(Json(new[] { Entry("a.txt") }, encrypted: true));
        string unar = Unar("echo \"  a.txt  (5 B)... Failed! (Cannot write $pw)\"\nexit 1");
        var r = Extractor(lsar, unar, allow: true).Run(Job(passwords: new[] { Secret }), CancellationToken.None);

        Assert.Equal(ExtractFailure.ToolFailed, r.Failure);
        Assert.Equal("Cannot write ***", r.Detail);
        Assert.DoesNotContain(Secret, ExtractText.Describe(r.State, r.Failure, r.Detail));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("wrong password.txt")]
    [InlineData("encrypted unsupported method.txt")]
    public void Crc_error_is_corrupt_whatever_the_file_is_called(string name)
    {
        string unar = Unar($"echo '  {name}  (5 B)... Failed! (Wrong checksum)'\necho 'Extraction to directory \"x\" failed (1 file failed.)'\nexit 1");
        var r = Extractor(Lsar(Json(new[] { Entry(name) })), unar).Run(Job(), CancellationToken.None);

        Assert.Equal(ExtractFailure.Corrupt, r.Failure);
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData(true, ExtractFailure.MissingParts)]
    [InlineData(false, ExtractFailure.Corrupt)]
    public void Incomplete_listing_is_missing_parts_only_for_a_set(bool multi, ExtractFailure expected)
    {
        string lsar = Lsar(Json(new[] { Entry("a.txt") }, error: 2));
        var r = Extractor(lsar, Unar(WriteA)).Run(Job(multi), CancellationToken.None);

        Assert.Equal(expected, r.Failure);
        Assert.Empty(Calls("unar"));
    }

    [Fact]
    public void Success_without_the_listed_files_is_not_done()
    {
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt") })), Unar("exit 0")).Run(Job(), CancellationToken.None);
        Assert.Equal(ExtractFailure.Corrupt, r.Failure);
    }

    [Fact]
    public void Not_enough_space_stops_before_unar_runs()
    {
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt", 1_000_000_000) })), Unar(WriteA), free: 10).Run(Job(), CancellationToken.None);
        Assert.Equal(ExtractFailure.NoSpace, r.Failure);
        Assert.Empty(Calls("unar"));
    }

    [Fact]
    public void A_hanging_unar_times_out_and_leaves_nothing()
    {
        var clock = Stopwatch.StartNew();
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt") })), Unar(WriteA + "\nexec sleep 60"), timeout: TimeSpan.FromSeconds(2))
            .Run(Job(), CancellationToken.None);

        Assert.Equal(ExtractFailure.Timeout, r.Failure);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void Cancel_stops_unar_and_leaves_nothing()
    {
        string started = _dir.File("laeuft");
        string unar = Unar($"{WriteA}\n: > '{started}'\nexec sleep 60");
        var extractor = Extractor(Lsar(Json(new[] { Entry("a.txt") })), unar);
        using var cts = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            for (int i = 0; i < 600 && !File.Exists(started); i++) Thread.Sleep(50);
            cts.Cancel();
        });
        watcher.Start();
        var clock = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => extractor.Run(Job(), cts.Token));

        watcher.Join();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(_out));
    }

    // ---------------------------------------------------------------- 7-Zip und bsdtar

    private const string WithoutRar = "echo\necho 'Formats:'\necho ' 0 C...F...  7z       7z    7 z BC AF'\necho ' 0 ........  Split    001'\necho\necho 'Codecs:'\necho ' 0 ED 40301 Rar1'";
    private const string WithRar = "echo 'Formats:'\necho ' 0 ........  Rar      rar r00   R a r ! 1A 07 00'\necho ' 0 ........  Rar5     rar r00   R a r !'\necho";

    private string SevenZip(string formats, string rest = "exit 2") => Script("7z", $"if [ \"$1\" = i ]; then\n{formats}\nexit 0\nfi\n{rest}");

    [Fact]
    public void A_7zip_without_rar_is_only_asked_for_its_formats()
    {
        string seven = SevenZip(WithoutRar);
        for (int i = 0; i < 2; i++)
        {
            var r = Extractor(null, null, sevenZip: seven).Run(Job(multi: true), CancellationToken.None);
            Assert.Equal(ExtractState.NeedRarTool, r.State);
            Assert.Equal(ExtractFailure.None, r.Failure);
        }
        Assert.Equal(new[] { "7z i" }, Calls("7z")); // gemerkt, und nie gegen das Archiv gestartet
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void Unar_without_lsar_does_not_count()
    {
        var r = Extractor(null, Unar(WriteA)).Run(Job(), CancellationToken.None);
        Assert.Equal(ExtractState.NeedRarTool, r.State);
        Assert.Empty(Calls("unar"));
    }

    [Fact]
    public void Zip_and_7z_never_ask_for_the_formats()
    {
        string archive = _dir.File("a.7z");
        File.WriteAllText(archive, "x");
        Extractor(null, null, sevenZip: SevenZip(WithoutRar))
            .Run(new ExtractJob(archive, _out, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.DoesNotContain("7z i", Calls("7z"));
    }

    [Fact]
    public void A_7zip_with_rar_takes_over_a_password_unar_may_not_get()
    {
        string stdin = _dir.File("stdin.log");
        string seven = SevenZip(WithRar, $"cat >> '{stdin}'\necho 'ERROR: Wrong password'\nexit 2");
        var r = Extractor(Lsar(Json(new[] { Entry("a.txt") }, encrypted: true)), Unar(WriteA), sevenZip: seven)
            .Run(Job(passwords: new[] { Secret }), CancellationToken.None);

        Assert.Equal(ExtractState.NeedPassword, r.State);
        Assert.Contains(Secret, File.ReadAllText(stdin));
        Assert.DoesNotContain(Secret, File.ReadAllText(_log));
    }

    [Fact]
    public void Bsdtar_never_follows_a_password_problem()
    {
        string tar = Script("bsdtar", "echo 'bsdtar: Encrypted file is unsupported' >&2\nexit 1");
        var withUnar = Extractor(Lsar(Json(new[] { Entry("a.txt") }, encrypted: true)), Unar(WriteA), tar: tar).Run(Job(), CancellationToken.None);
        Assert.Equal(ExtractFailure.PasswordNeedsTool, withUnar.Failure);
        Assert.Empty(Calls("bsdtar"));

        var alone = Extractor(null, null, tar: tar).Run(Job(), CancellationToken.None);
        Assert.Equal(ExtractState.NeedRarTool, alone.State); // nach einem Passwort zu fragen, hülfe nicht
    }

    // ---------------------------------------------------------------- Erkennung von RAR in 7-Zip

    [Fact]
    public void Rar_support_is_read_from_the_formats_section_only()
    {
        Assert.True(ToolLocator.SevenZipSupportsRar("Libs:\n\nFormats:\n 0 ....  Rar5   rar r00   R a r !\n\nCodecs:\n"));
        Assert.True(ToolLocator.SevenZipSupportsRar("Formats:\r\n   C...F  Rar   rar r00\r\n\r\n"));
        Assert.False(ToolLocator.SevenZipSupportsRar("Formats:\n 0 ....  zip  zip rar\n\nCodecs:\n 0 ED 40303 Rar3\n 0 D 40305 Rar5\n"));
        Assert.False(ToolLocator.SevenZipSupportsRar("Rar Rar5"));
        Assert.False(ToolLocator.SevenZipSupportsRar(""));
    }

    [Fact]
    public void New_state_and_reason_survive_saving_and_stay_at_the_end()
    {
        Assert.Equal(ExtractState.NeedRarTool, Enum.GetValues<ExtractState>().Last());
        Assert.Equal(ExtractFailure.PasswordNeedsTool, Enum.GetValues<ExtractFailure>().Last());

        var item = new DownloadItem("http://127.0.0.1/a.rar", "a.rar", _dir.Path);
        item.SetExtract(ExtractState.NeedRarTool);
        Assert.Equal(ExtractState.NeedRarTool, DownloadItem.FromEntry(item.ToEntry()).Extract);
        Assert.True(item.StateIsProblem);
        item.SetExtract(ExtractState.Failed, ExtractFailure.PasswordNeedsTool);
        Assert.Equal(ExtractFailure.PasswordNeedsTool, DownloadItem.FromEntry(item.ToEntry()).ExtractReason);
        Assert.Contains("unar", ExtractText.Describe(item.Extract, item.ExtractReason, null));
        Assert.Contains("Einstellungen", ExtractText.Describe(item.Extract, item.ExtractReason, null));
    }

    [Fact]
    public void Password_on_the_command_line_is_off_by_default_and_is_saved()
    {
        var s = new AppSettings();
        Assert.False(s.AllowUnarPasswordOnCommandLine);
        s.AllowUnarPasswordOnCommandLine = true;
        string path = _dir.File("settings.json");
        AppSettings.Write(path, s.ToJson());
        Assert.True(AppSettings.Load(path).AllowUnarPasswordOnCommandLine);
    }
}
