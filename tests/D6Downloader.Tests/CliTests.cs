using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Browser;
using D6Downloader.Engine;
using Xunit;

namespace D6Downloader.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--autostart")]
    public void Only_a_plain_start_or_autostart_opens_the_app(string arguments) =>
        Assert.Equal(CommandKind.App, CommandLine.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Kind);

    [Theory]
    [InlineData("--verison")]
    [InlineData("--version extra")]
    [InlineData("--autostart --verbose")]
    [InlineData("http://127.0.0.1/a.zip")]
    [InlineData("--add")]
    [InlineData("--add datei.zip")]
    [InlineData("--add http://127.0.0.1/a --out")]
    [InlineData("--add file:///etc/passwd")]
    [InlineData("--headless-download")]
    [InlineData("--headless-download http://127.0.0.1/a")]
    [InlineData("--headless-download http://127.0.0.1/a --in x")]
    [InlineData("--headless-download ftp://127.0.0.1/a --out x")]
    public void Anything_else_is_refused(string arguments)
    {
        var command = CommandLine.Parse(arguments.Split(' '));
        Assert.Equal(CommandKind.Invalid, command.Kind);
        Assert.False(string.IsNullOrEmpty(command.Problem));
    }

    [Fact]
    public void Commands_carry_their_arguments()
    {
        Assert.Equal(CommandKind.Version, CommandLine.Parse(new[] { "--version" }).Kind);
        Assert.Equal(CommandKind.Help, CommandLine.Parse(new[] { "--help" }).Kind);
        var add = CommandLine.Parse(new[] { "--add", "http://127.0.0.1/a", "https://nirgends.invalid/b" });
        Assert.Equal(CommandKind.Add, add.Kind);
        Assert.Equal(new[] { "http://127.0.0.1/a", "https://nirgends.invalid/b" }, add.Urls);
        var headless = CommandLine.Parse(new[] { "--headless-download", "http://127.0.0.1/a", "--out", "ziel ordner" });
        Assert.Equal(CommandKind.HeadlessDownload, headless.Kind);
        Assert.Equal("ziel ordner", headless.OutDir);
    }

    private sealed class Link : IAppLink
    {
        public bool Running, LaunchWorks = true;
        public int Launches;
        public string? Received;
        public bool TrySend(string text)
        {
            if (Running) Received = text;
            return Running;
        }
        public bool Launch()
        {
            Launches++;
            Running = LaunchWorks;
            return LaunchWorks;
        }
    }

    [Fact]
    public void Add_hands_all_links_to_the_app_and_starts_it_only_if_needed()
    {
        var urls = new[] { "http://127.0.0.1/a", "http://127.0.0.1/b" };
        var running = new Link { Running = true };
        var closed = new Link();
        var broken = new Link { LaunchWorks = false };
        var errors = new StringWriter();

        Assert.Equal(0, CommandLine.Add(urls, running, TimeSpan.FromSeconds(1), errors));
        Assert.Equal(0, CommandLine.Add(urls, closed, TimeSpan.FromSeconds(1), errors));
        Assert.Equal("", errors.ToString());
        Assert.Equal(1, CommandLine.Add(urls, broken, TimeSpan.FromSeconds(1), errors));

        Assert.Equal(0, running.Launches);
        Assert.Equal(1, closed.Launches);
        Assert.Equal("http://127.0.0.1/a\nhttp://127.0.0.1/b", closed.Received);
        Assert.NotEqual("", errors.ToString());
    }
}

/// <summary>Nur gegen den Testserver auf 127.0.0.1.</summary>
public sealed class HeadlessDownloadTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<(int Code, string Output, string Errors)> Run(string url, string folder)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        int code = await HeadlessDownload.RunAsync(url, folder, output, errors, CancellationToken.None);
        return (code, output.ToString().TrimEnd('\n'), errors.ToString());
    }

    [Fact]
    public async Task Downloads_the_file_and_never_touches_what_is_already_there()
    {
        using var server = new TestServer(300_000);
        string foreign = _dir.File("file.bin");
        File.WriteAllText(foreign + ".part", "Teildaten der App");
        File.WriteAllText(_dir.File("file (1).bin"), "fertig");

        var result = await Run(server.Url, _dir.Path);

        Assert.Equal(0, result.Code);
        Assert.Equal(_dir.Path, Path.GetDirectoryName(result.Output));
        Assert.Equal(server.Data, File.ReadAllBytes(result.Output));
        Assert.Equal("Teildaten der App", File.ReadAllText(foreign + ".part"));
        Assert.Equal("fertig", File.ReadAllText(_dir.File("file (1).bin")));
        Assert.False(File.Exists(foreign));
    }

    [Fact]
    public async Task A_second_run_gets_a_numbered_name()
    {
        using var server = new TestServer(1000);

        var first = await Run(server.Url, _dir.Path);
        var second = await Run(server.Url, _dir.Path);

        Assert.Equal(0, second.Code);
        Assert.NotEqual(first.Output, second.Output);
        Assert.Contains(" (1)", second.Output);
    }

    [Fact]
    public async Task Rapidgator_is_refused_without_any_request()
    {
        var result = await Run("https://rapidgator.net/file/0123456789abcdef0123456789abcdef", _dir.Path);

        Assert.Equal(1, result.Code);
        Assert.Contains("Rapidgator", result.Errors);
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path));
    }

    [Fact]
    public void One_target_has_one_writer_also_through_a_linked_folder()
    {
        Directory.CreateDirectory(_dir.File("echt"));
        Directory.CreateSymbolicLink(_dir.File("link"), _dir.File("echt"));
        using (var held = TargetReservation.TryAcquire(_dir.File("echt/a.bin")))
        {
            Assert.NotNull(held);
            Assert.Null(TargetReservation.TryAcquire(_dir.File("link/a.bin")));
            using var other = TargetReservation.TryAcquire(_dir.File("echt/b.bin"));
            Assert.NotNull(other);
        }
        using var again = TargetReservation.TryAcquire(_dir.File("link/a.bin"));
        Assert.NotNull(again);
    }

    /// <summary>Der echte zweite Prozess: Solange er schreibt, bekommt dieser Prozess dieselbe .part nicht.</summary>
    [Fact]
    public async Task A_headless_process_and_the_engine_never_share_a_part_file()
    {
        using var server = new TestServer(2_000_000) { ThrottleBytesPerSecond = 50_000 };
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { Path.Combine(AppContext.BaseDirectory, "d6downloader.dll"), "--headless-download", server.Url, "--out", _dir.Path })
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        try
        {
            var clock = Stopwatch.StartNew();
            string[] parts;
            while ((parts = Directory.GetFiles(_dir.Path, "*.part")).Length == 0)
            {
                Assert.False(process.HasExited, "beendet, bevor eine .part da war");
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "keine .part");
                await Task.Delay(50);
            }
            string target = parts[0][..^".part".Length];
            using var engine = new HttpDownloader(new DownloadOptions());

            var error = await Assert.ThrowsAsync<StorageException>(() =>
                engine.DownloadAsync(new DownloadRequest(server.Url, target), null, CancellationToken.None));

            Assert.Contains(Loc.T("Error.TargetBusy"), error.Message);
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }
}
