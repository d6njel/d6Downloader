using System.IO;
using D6Downloader.Platform;
using D6Downloader.Platform.Linux;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Immer mit <see cref="FakeRunner"/>: <c>gio</c> wird nie gestartet.</summary>
public class GioTrashTests
{
    [Fact]
    public void Calls_gio_trash_with_the_path_after_a_double_dash()
    {
        var runner = new FakeRunner();
        using var dir = new TempDir();
        string path = dir.File("a b.zip");

        new GioTrash(runner).MoveToTrash(path);

        Assert.Equal("gio trash -- " + path, Assert.Single(runner.Calls));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void A_failure_or_timeout_throws(int? exitCode)
    {
        var runner = new FakeRunner { Answer = _ => new(exitCode, "") };

        Assert.Throws<IOException>(() => new GioTrash(runner).MoveToTrash("/nirgends/a.zip"));
    }

    [Fact]
    public void A_file_that_is_still_there_throws_and_stays()
    {
        using var dir = new TempDir();
        string path = dir.File("a.zip");
        File.WriteAllText(path, "x");

        Assert.Throws<IOException>(() => new GioTrash(new FakeRunner()).MoveToTrash(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Without_gio_it_throws_and_starts_nothing()
    {
        var runner = new FakeRunner();
        runner.Missing.Add("gio");

        Assert.Throws<IOException>(() => new GioTrash(runner).MoveToTrash("/nirgends/a.zip"));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void The_default_trash_of_the_tests_never_moves_anything()
    {
        Assert.Same(NoTrash.Instance, PlatformServices.Trash);
        Assert.Throws<IOException>(() => PlatformServices.Trash.MoveToTrash("/nirgends/a.zip"));
    }
}
