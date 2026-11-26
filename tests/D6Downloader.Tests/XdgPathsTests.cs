using System.Collections.Generic;
using System.IO;
using Xunit;

namespace D6Downloader.Tests;

public class XdgPathsTests
{
    private static XdgPaths With(params (string, string)[] variables)
    {
        var env = new Dictionary<string, string>();
        foreach (var (k, v) in variables) env[k] = v;
        return new XdgPaths(name => env.GetValueOrDefault(name));
    }

    [Fact]
    public void Config_home_is_absolute_even_when_it_does_not_exist()
    {
        using var dir = new TempDir();

        Assert.Equal(Path.Combine(dir.Path, ".config", "d6downloader"), With(("HOME", dir.Path)).AppConfigDir);
        // Relative Werte zählen nach der Spezifikation nicht.
        Assert.Equal(Path.Combine(dir.Path, ".config"), With(("HOME", dir.Path), ("XDG_CONFIG_HOME", "rel")).ConfigHome);
        Assert.Equal("/x/cfg", With(("HOME", dir.Path), ("XDG_CONFIG_HOME", "/x/cfg")).ConfigHome);
        Assert.Null(With(("HOME", dir.Path), ("XDG_RUNTIME_DIR", "run")).RuntimeDir);
    }

    [Theory]
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME/Herunterladen\"", "Herunterladen")]
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME/Mit \\\"Zeichen\\\"\"", "Mit \"Zeichen\"")]
    [InlineData("# XDG_DOWNLOAD_DIR=\"$HOME/Nein\"", "Downloads")]
    [InlineData("XDG_DOWNLOAD_DIR=\"relativ\"", "Downloads")]
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME/\"", "Downloads")] // abgeschaltet
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME/offen", "Downloads")]
    public void Download_folder_follows_user_dirs(string line, string expected)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File(".config"));
        File.WriteAllText(dir.File(".config/user-dirs.dirs"), "XDG_MUSIC_DIR=\"$HOME/Musik\"\n" + line + "\n");

        Assert.Equal(Path.Combine(dir.Path, expected), With(("HOME", dir.Path)).DownloadDir);
    }

    [Fact]
    public void Download_folder_may_be_absolute_and_has_a_fallback()
    {
        using var dir = new TempDir();
        var xdg = With(("HOME", dir.Path), ("XDG_CONFIG_HOME", dir.Path));
        Assert.Equal(Path.Combine(dir.Path, "Downloads"), xdg.DownloadDir);

        File.WriteAllText(dir.File("user-dirs.dirs"), "XDG_DOWNLOAD_DIR=\"/mnt/platte\"\n");
        Assert.Equal("/mnt/platte", xdg.DownloadDir);
    }

    [Fact]
    public void Data_folder_is_private_and_inside_the_test_home()
    {
        string data = AppPaths.DataDir;

        Assert.StartsWith(TestSetup.Home, data);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));
    }
}
