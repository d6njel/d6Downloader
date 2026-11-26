using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using D6Downloader.Remote;
using Xunit;

namespace D6Downloader.Tests;

public class RemoteStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static long Ms(TimeSpan offset) => (Now + offset).ToUnixTimeMilliseconds();

    [Fact]
    public void An_id_is_accepted_once_also_after_a_restart()
    {
        using var dir = new TempDir();
        string file = dir.File("remote-state.json");
        var store = new ReplayStore(() => file, () => Now);

        Assert.True(store.TryAccept("a", "1", Ms(default), 1000));
        Assert.False(store.TryAccept("a", "1", Ms(default), 1000));
        Assert.True(store.TryAccept("b", "1", Ms(default), 900));

        var restarted = new ReplayStore(() => file, () => Now);
        Assert.False(restarted.TryAccept("a", "1", Ms(default), 2000));
        Assert.Equal(1000, restarted.LastTime);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void Only_the_time_window_is_accepted_and_old_ids_are_dropped()
    {
        using var dir = new TempDir();
        var now = Now;
        var store = new ReplayStore(() => dir.File("s.json"), () => now);

        Assert.False(store.TryAccept("a", "alt", Ms(TimeSpan.FromHours(-14)), 1));
        Assert.False(store.TryAccept("a", "zukunft", Ms(TimeSpan.FromMinutes(11)), 1));
        Assert.True(store.TryAccept("a", "x", Ms(TimeSpan.FromHours(-12)), 1));
        Assert.True(store.TryAccept("a", "y", Ms(TimeSpan.FromMinutes(9)), 1));
        Assert.Equal(2, store.Count("a"));

        now += TimeSpan.FromHours(2); // „x“ ist jetzt 14 h alt
        Assert.True(store.TryAccept("a", "z", now.ToUnixTimeMilliseconds(), 1));
        Assert.Equal(2, store.Count("a"));
    }

    [Fact]
    public void Nothing_is_accepted_if_it_cannot_be_saved()
    {
        using var dir = new TempDir();
        var store = new ReplayStore(() => dir.File("fehlt/s.json"), () => Now);
        Assert.False(store.TryAccept("a", "1", Ms(default), 1));
        Assert.Equal(0, store.Count("a"));
        Assert.Null(store.LastTime);
    }

    [Fact]
    public void After_a_damaged_file_older_messages_are_refused()
    {
        using var dir = new TempDir();
        string file = dir.File("s.json");
        File.WriteAllText(file, "{kaputt");
        var store = new ReplayStore(() => file, () => Now);

        Assert.False(store.TryAccept("a", "vorher", Ms(TimeSpan.FromMinutes(-1)), 1));
        Assert.True(store.TryAccept("a", "danach", Ms(TimeSpan.FromSeconds(1)), 1));
        Assert.False(new ReplayStore(() => file, () => Now).TryAccept("a", "vorher", Ms(TimeSpan.FromMinutes(-1)), 1));
    }

    [Theory]
    [InlineData("http://localhost/x")]
    [InlineData("http://LOCALHOST./x")]
    [InlineData("http://router/x")]
    [InlineData("http://drucker.local/x")]
    [InlineData("http://nas.lan/x")]
    [InlineData("http://a.home/x")]
    [InlineData("http://a.internal/x")]
    [InlineData("http://fritz.box/x")]
    [InlineData("http://nas.fritz.box/x")]
    [InlineData("http://a.home.arpa/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://127.5.5.5:8080/x")]
    [InlineData("http://2130706433/x")]
    [InlineData("http://0x7f.0.0.1/x")]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://172.16.0.1/x")]
    [InlineData("http://172.31.255.255/x")]
    [InlineData("http://192.168.178.1/x")]
    [InlineData("http://169.254.1.1/x")]
    [InlineData("http://100.64.0.1/x")]
    [InlineData("http://0.0.0.0/x")]
    [InlineData("http://224.0.0.1/x")]
    [InlineData("http://255.255.255.255/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://[::]/x")]
    [InlineData("http://[fc00::1]/x")]
    [InlineData("http://[fd12:3456::1]/x")]
    [InlineData("http://[fe80::1]/x")]
    [InlineData("http://[ff02::1]/x")]
    [InlineData("http://[::ffff:192.168.0.1]/x")]
    [InlineData("http://[64:ff9b::7f00:1]/x")]
    [InlineData("ftp://example.com/x")]
    public async Task Local_and_private_targets_are_blocked(string url)
    {
        var guard = new TargetGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        Assert.Equal(TargetVerdict.Blocked, await guard.CheckAsync(url));
    }

    [Theory]
    [InlineData("http://8.8.8.8/x")]
    [InlineData("http://172.32.0.1/x")]
    [InlineData("http://100.128.0.1/x")]
    [InlineData("https://[2606:4700::1111]/x")]
    [InlineData("https://example.com/x")]
    [InlineData("https://mylan.example/x")]
    public async Task Public_targets_are_allowed(string url)
    {
        var guard = new TargetGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        Assert.Equal(TargetVerdict.Allowed, await guard.CheckAsync(url));
    }

    [Fact]
    public async Task A_name_resolving_to_a_private_address_is_blocked_and_an_unknown_name_is_unresolved()
    {
        var guard = new TargetGuard((host, _) => host == "boese.example.com"
            ? Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.0.10") })
            : throw new SocketException((int)SocketError.HostNotFound));
        Assert.Equal(TargetVerdict.Blocked, await guard.CheckAsync("https://boese.example.com/x"));
        Assert.Equal(TargetVerdict.Unresolved, await guard.CheckAsync("https://gibtsnicht.example.com/x"));
    }
}
