using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Browser;
using D6Downloader.Platform.Linux;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Registrierung und Host-Start so, wie ein Browser sie benutzt, in einem eigenen HOME. Nie in echten Profilen.</summary>
public sealed class BrowserHostTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private XdgPaths Xdg() => new(n => n == "HOME" ? _dir.Path : null);

    [Theory]
    [InlineData(BrowserKind.Chrome, ".config/chromium")]
    [InlineData(BrowserKind.Firefox, ".mozilla")]
    public async Task The_registered_program_answers_when_started_like_the_browser_does(BrowserKind kind, string profile)
    {
        Directory.CreateDirectory(_dir.File(profile));
        string program = Path.Combine(AppContext.BaseDirectory, "d6downloader");
        var setup = new BrowserSetup(new LinuxNativeHostLocations(Xdg()), program);
        setup.Register(kind);
        string manifestPath = Assert.Single(setup.ManifestPaths(kind));
        Assert.StartsWith(_dir.Path, manifestPath);
        var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
        Assert.Equal(Path.GetFileNameWithoutExtension(manifestPath), manifest.GetProperty("name").GetString());

        var info = new ProcessStartInfo(manifest.GetProperty("path").GetString()!)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        // Chrome nennt den Ursprung, Firefox die Manifest-Datei und die Erweiterung.
        if (kind == BrowserKind.Chrome) info.ArgumentList.Add(manifest.GetProperty("allowed_origins")[0].GetString()!);
        else
        {
            info.ArgumentList.Add(manifestPath);
            info.ArgumentList.Add(manifest.GetProperty("allowed_extensions")[0].GetString()!);
        }
        info.Environment["XDG_RUNTIME_DIR"] = _dir.Path;
        // Ohne Bildschirm: Selbst ein Fehler könnte so kein Fenster öffnen.
        info.Environment.Remove("DISPLAY");
        info.Environment.Remove("WAYLAND_DISPLAY");
        using var host = Process.Start(info)!;
        try
        {
            NativeMessage.Write(host.StandardInput.BaseStream, Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"));
            var read = Task.Run(() => NativeMessage.Read(host.StandardOutput.BaseStream));
            var reply = JsonDocument.Parse((await read.WaitAsync(TimeSpan.FromSeconds(30)))!).RootElement;
            Assert.True(reply.GetProperty("ok").GetBoolean());
            Assert.False(reply.GetProperty("running").GetBoolean());
            host.StandardInput.Close();
            Assert.True(host.WaitForExit(10_000));
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (!host.HasExited) host.Kill();
        }
    }

    /// <summary>
    /// Der echte Start über <c>setsid</c>, mit einem Skript an der Stelle der App: eigene Sitzung (der Browser beendet nur die
    /// Gruppe des Hosts) und keine Datenströme des Browsers.
    /// </summary>
    [Fact]
    public void Launch_detaches_the_app_into_its_own_session_without_the_browser_pipes()
    {
        string report = _dir.File("bericht");
        Directory.CreateDirectory(_dir.File("Ordner mit Leerzeichen"));
        string app = _dir.File("Ordner mit Leerzeichen/app");
        File.WriteAllText(app,
            "#!/bin/sh\n" +
            "read -r pid comm state ppid pgrp session rest < /proc/$$/stat\n" +
            // Vor der Umleitung von echo lesen, sonst zeigte fd 1 auf den Bericht.
            "in=$(readlink /proc/$$/fd/0); out=$(readlink /proc/$$/fd/1)\n" +
            $"echo \"$$ $pgrp $session $in $out\" > '{report}.tmp'\n" +
            $"mv '{report}.tmp' '{report}'\n" +
            "exec sleep 20\n");
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(new UnixSocketAppLink(new RuntimePaths(_dir.File("run")), app).Launch());

        var clock = Stopwatch.StartNew();
        while (!File.Exists(report) && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
        string[] fields = File.ReadAllText(report).Trim().Split(' ');
        int pid = int.Parse(fields[0]);
        try
        {
            string own = File.ReadAllText("/proc/self/stat");
            string ownSession = own[(own.LastIndexOf(')') + 2)..].Split(' ')[3];
            Assert.Equal(fields[0], fields[1]); // Anführer der eigenen Prozessgruppe
            Assert.Equal(fields[0], fields[2]); // und der eigenen Sitzung
            Assert.NotEqual(ownSession, fields[2]);
            Assert.Equal(new[] { "/dev/null", "/dev/null" }, fields.Skip(3));
        }
        finally
        {
            Process.GetProcessById(pid).Kill();
        }
    }
}
