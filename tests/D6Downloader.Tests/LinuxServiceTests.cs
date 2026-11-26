using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using D6Downloader.Browser;
using D6Downloader.Platform.Linux;
using D6Downloader.Power;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Merkt sich nur, was gestartet würde. Damit löst kein Test etwas am System aus.</summary>
internal sealed class FakeRunner : ICommandRunner
{
    public readonly List<string> Calls = new();
    public readonly List<FakeHeld> Held = new();
    public readonly HashSet<string> Missing = new();
    public Func<string, CommandResult> Answer = _ => new(0, "");

    internal sealed class FakeHeld : IHeldProcess
    {
        public bool HasExited { get; set; }
        public bool Disposed;
        public void Dispose() => Disposed = HasExited = true;
    }

    private static string Line(string exe, IReadOnlyList<string> args) => string.Join(' ', args.Prepend(Path.GetFileName(exe)));

    public string? Find(string name) => Missing.Contains(name) ? null : "/fake/" + name;

    public CommandResult Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Calls.Add(Line(exe, args));
        return Answer(Calls[^1]);
    }

    public void Start(string exe, IReadOnlyList<string> args) => Calls.Add(Line(exe, args));

    public IHeldProcess Hold(string exe, IReadOnlyList<string> args)
    {
        Calls.Add(Line(exe, args));
        Held.Add(new FakeHeld());
        return Held[^1];
    }
}

public class KeepAwakeTests
{
    [Fact]
    public void Holds_one_inhibitor_while_on_and_releases_it()
    {
        var runner = new FakeRunner();
        var awake = new SystemdInhibitKeepAwake(runner);

        awake.Set(true);
        awake.Set(true);
        Assert.Equal("systemd-inhibit --what=sleep:idle --who=d6Downloader --why=Downloads --mode=block --no-ask-password /fake/cat",
            Assert.Single(runner.Calls));
        awake.Set(false);
        Assert.True(runner.Held[0].Disposed);
        awake.Set(true);
        Assert.Equal(2, runner.Held.Count);
        awake.Dispose();
        Assert.True(runner.Held[1].Disposed);
    }

    [Fact]
    public void A_refused_inhibitor_is_retried_only_after_a_while()
    {
        var runner = new FakeRunner();
        var now = new DateTime(2030, 1, 1);
        var awake = new SystemdInhibitKeepAwake(runner, () => now);

        awake.Set(true);
        runner.Held[0].HasExited = true;
        awake.Set(true);
        Assert.Single(runner.Held);
        now = now.AddMinutes(2);
        awake.Set(true);
        Assert.Equal(2, runner.Held.Count);
    }

    /// <summary>Echter, harmloser Kindprozess: cat endet, sobald die Standardeingabe geschlossen wird.</summary>
    [Fact]
    public void A_held_process_lives_until_disposed()
    {
        var held = CommandRunner.Instance.Hold(CommandRunner.Instance.Find("cat")!, Array.Empty<string>());
        Assert.False(held.HasExited);
        held.Dispose();
        Assert.True(held.HasExited);
    }
}

public class LinuxPowerTests
{
    private const string Kde = "busctl --user call org.kde.Shutdown /Shutdown org.kde.Shutdown logoutAndShutdown";

    [Fact]
    public void Shutdown_prefers_kde()
    {
        var runner = new FakeRunner();

        Assert.Null(new LinuxPowerActions(runner).Execute(PowerAction.Shutdown));

        Assert.Equal(Kde, Assert.Single(runner.Calls));
    }

    [Fact]
    public void Shutdown_falls_back_to_systemctl_and_reports_a_refusal()
    {
        var runner = new FakeRunner { Answer = line => new(line == Kde ? 1 : 4, "") };

        Assert.Equal(Loc.T("Power.Refused", 4), new LinuxPowerActions(runner).Execute(PowerAction.Shutdown));

        Assert.Equal(new[] { Kde, "systemctl --no-ask-password poweroff" }, runner.Calls);
    }

    [Theory]
    [InlineData("yes", true, true)]
    [InlineData("na", false, false)]
    [InlineData("no", false, false)]
    [InlineData("challenge", false, true)]
    public void Hibernate_needs_a_clear_yes(string answer, bool hibernate, bool sleep)
    {
        var runner = new FakeRunner { Answer = _ => new(0, $"s \"{answer}\"\n") };
        var power = new LinuxPowerActions(runner);

        Assert.Equal(hibernate, power.IsAvailable(PowerAction.Hibernate));
        Assert.Equal(sleep, power.IsAvailable(PowerAction.Sleep));
        Assert.False(power.IsAvailable(PowerAction.None));
        Assert.Equal(hibernate ? null : Loc.T("After.NoHibernate"), power.Execute(PowerAction.Hibernate));
        Assert.Equal(hibernate, runner.Calls.Contains("systemctl --no-ask-password hibernate"));
    }

    [Fact]
    public void Sleep_uses_systemctl_and_missing_tools_are_reported()
    {
        var runner = new FakeRunner();
        Assert.Null(new LinuxPowerActions(runner).Execute(PowerAction.Sleep));
        Assert.Equal("systemctl --no-ask-password suspend", Assert.Single(runner.Calls));

        runner.Missing.Add("systemctl");
        Assert.Equal(Loc.T("Power.StartFailed"), new LinuxPowerActions(runner).Execute(PowerAction.Sleep));
    }
}

public class XdgDesktopTests
{
    private static XdgPaths Xdg(TempDir dir) => new(n => n == "HOME" ? dir.Path : null);

    [Fact]
    public void Autostart_writes_and_removes_the_desktop_file()
    {
        using var dir = new TempDir();
        var autostart = new XdgAutostart(Xdg(dir), "/opt/Mein Ordner/d6downloader");
        string file = dir.File(".config/autostart/d6downloader.desktop");
        Assert.False(autostart.IsEnabled());

        autostart.Set(true);
        Assert.True(autostart.IsEnabled());
        Assert.Contains("\nExec=\"/opt/Mein Ordner/d6downloader\" --autostart\n", File.ReadAllText(file));

        File.AppendAllText(file, "Hidden=true\n");
        Assert.False(autostart.IsEnabled());
        autostart.Set(true);
        Assert.True(autostart.IsEnabled());

        autostart.Set(false);
        Assert.False(File.Exists(file));
        autostart.Set(false);
    }

    [Theory]
    [InlineData("/a/b", "\"/a/b\"")]
    [InlineData("/a/$x", "\"/a/\\\\$x\"")]
    [InlineData("/a/\"q\"", "\"/a/\\\\\"q\\\\\"\"")]
    [InlineData("/a/b\\c", "\"/a/b\\\\\\\\c\"")]
    [InlineData("/a/100%", "\"/a/100%%\"")]
    public void Exec_arguments_are_escaped(string path, string expected) => Assert.Equal(expected, XdgAutostart.Quote(path));

    [Fact]
    public void Manifests_go_only_into_existing_browser_profiles()
    {
        using var dir = new TempDir();
        var setup = new BrowserSetup(new LinuxNativeHostLocations(Xdg(dir)), "/opt/d6downloader");
        Assert.Empty(setup.ManifestPaths(BrowserKind.Chrome));
        Assert.Empty(setup.ManifestPaths(BrowserKind.Firefox));
        setup.Register(BrowserKind.Chrome);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));

        Directory.CreateDirectory(dir.File(".config/chromium"));
        Directory.CreateDirectory(dir.File(".config/BraveSoftware/Brave-Browser"));
        Directory.CreateDirectory(dir.File(".mozilla"));
        setup.Register(BrowserKind.Chrome);
        setup.Register(BrowserKind.Firefox);

        Assert.Equal(new[]
        {
            dir.File(".config/chromium/NativeMessagingHosts/d6downloader.host.json"),
            dir.File(".config/BraveSoftware/Brave-Browser/NativeMessagingHosts/d6downloader.host.json"),
        }, setup.ManifestPaths(BrowserKind.Chrome));
        Assert.True(File.Exists(dir.File(".mozilla/native-messaging-hosts/d6downloader.host.json")));
        Assert.Equal(BrowserState.Registered, setup.GetState(BrowserKind.Chrome));
        Assert.False(Directory.Exists(dir.File(".config/google-chrome")));
    }

    [Fact]
    public void Show_in_folder_asks_the_file_manager_and_falls_back_to_the_folder()
    {
        var runner = new FakeRunner();
        var shell = new XdgShell(runner);

        shell.ShowInFolder("/tmp/a b/#1 50%?.zip");
        Assert.Equal("busctl --user call org.freedesktop.FileManager1 /org/freedesktop/FileManager1 org.freedesktop.FileManager1 " +
                     "ShowItems ass 1 file:///tmp/a%20b/%231%2050%25%3F.zip ", Assert.Single(runner.Calls));

        runner.Answer = _ => new(1, "");
        shell.ShowInFolder("/tmp/x/y.zip");
        Assert.Equal("xdg-open /tmp/x", runner.Calls[^1]);
    }

    [Fact]
    public void Open_takes_only_absolute_paths_and_web_addresses()
    {
        var runner = new FakeRunner();
        var shell = new XdgShell(runner);

        shell.Open("https://example.org/");
        shell.Open("/tmp/a");
        Assert.Throws<IOException>(() => shell.Open("--help"));
        Assert.Throws<IOException>(() => shell.Open("relativ/pfad"));

        Assert.Equal(new[] { "xdg-open https://example.org/", "xdg-open /tmp/a" }, runner.Calls);
    }

    /// <summary>Echter Start eines selbst geschriebenen Skripts: eigene Sitzung, nichts landet auf den Datenströmen.</summary>
    [Fact]
    public void Launch_starts_the_app_in_its_own_session_without_our_streams()
    {
        using var dir = new TempDir();
        string app = dir.File("fake app");
        File.WriteAllText(app, "#!/bin/sh\necho \"$(readlink /proc/$$/fd/0) $(readlink /proc/$$/fd/1) $(ps -o sid= -p $$)\" > \"$0.tmp\"\nmv \"$0.tmp\" \"$0.out\"\n");
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(new UnixSocketAppLink(new RuntimePaths(dir.File("run")), app).Launch());

        for (int i = 0; i < 100 && !File.Exists(app + ".out"); i++) Thread.Sleep(50);
        string[] seen = File.ReadAllText(app + ".out").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("/dev/null", seen[0]);
        Assert.Equal("/dev/null", seen[1]);
        string ownSession = File.ReadAllText("/proc/self/stat").Split(") ")[1].Split(' ')[3];
        Assert.NotEqual(ownSession, seen[2].Trim());
    }
}
