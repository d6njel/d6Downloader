using System;
using System.Linq;
using System.Text;
using D6Downloader.Debrid;
using D6Downloader.Remote;
using Xunit;

namespace D6Downloader.Tests;

public class RemoteSettingsTests
{
    [Fact]
    public void Remote_reception_is_off_by_default()
    {
        var remote = new AppSettings().Remote;
        Assert.False(remote.Enabled);
        Assert.False(remote.AutoStart);
        Assert.Equal("https://ntfy.sh", remote.ServerUrl);
        Assert.Empty(remote.Devices);
    }

    [Fact]
    public void Settings_round_trip_with_the_seed_only_encrypted()
    {
        using var dir = new TempDir();
        var settings = new AppSettings();
        string seed = Convert.ToBase64String(RemoteCryptoTests.Seed);
        settings.Remote.Enabled = true;
        settings.Remote.AutoStart = true;
        settings.Remote.ServerUrl = "https://ntfy.example.org";
        settings.Remote.Devices.Add(new RemoteDevice
        {
            DeviceId = "jx837f4p",
            Name = "Pixel",
            SeedProtected = SecretProtector.Protect(seed),
            PairedUtc = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc),
        });
        AppSettings.Write(dir.File("settings.json"), settings.ToJson());

        string json = System.IO.File.ReadAllText(dir.File("settings.json"));
        Assert.DoesNotContain(seed, json);
        var loaded = AppSettings.Load(dir.File("settings.json")).Remote;
        Assert.True(loaded.Enabled);
        Assert.True(loaded.AutoStart);
        Assert.Equal("https://ntfy.example.org", loaded.ServerUrl);
        var device = Assert.Single(loaded.Devices);
        Assert.StartsWith("v1:", device.SeedProtected);
        Assert.Equal(seed, SecretProtector.Unprotect(device.SeedProtected));
    }

    [Fact]
    public void Null_values_written_by_hand_fall_back_to_the_defaults()
    {
        using var dir = new TempDir();
        System.IO.File.WriteAllText(dir.File("s.json"), """{"remote":{"serverUrl":null,"devices":null}}""");
        var remote = AppSettings.Load(dir.File("s.json")).Remote;
        Assert.Equal("https://ntfy.sh", remote.ServerUrl);
        Assert.Empty(remote.Devices);
    }
}
