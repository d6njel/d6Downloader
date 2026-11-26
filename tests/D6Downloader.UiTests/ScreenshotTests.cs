using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using D6Downloader.Downloads;
using D6Downloader.Engine;
using D6Downloader.Packages;
using D6Downloader.Sheets;
using D6Downloader.Tests;
using Xunit;

namespace D6Downloader.UiTests;

/// <summary>
/// Zeichnet Bildschirmfotos ohne Bildschirm, nur wenn <c>D6_SCREENSHOTS</c> auf einen Ordner zeigt:
/// <c>D6_SCREENSHOTS=$PWD/docs/screenshots dotnet test tests/D6Downloader.UiTests</c>. Alle Daten sind ausgedacht.
/// </summary>
public class ScreenshotTests
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("D6_SCREENSHOTS");

    private static void Save(Ui ui, string name)
    {
        Ui.Pump();
        Ui.Pump();
        using var frame = ui.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(Target!);
        frame.Save(Path.Combine(Target!, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Fill(Ui ui)
    {
        const long mb = 1024 * 1024;
        DownloadItem Item(string name, long size, string? category = null, string? package = null) =>
            new("https://example.invalid/" + name, name, "/home/user/Downloads")
            {
                TotalBytes = size, Category = category, PackageId = package, PackageName = package,
            };

        var running = Item("fedora-beispiel.iso", 2100 * mb, "builtin:archives");
        var one = Item("Urlaubsfilm.part1.rar", 500 * mb, null, "Urlaubsfilm");
        var two = Item("Urlaubsfilm.part2.rar", 500 * mb, null, "Urlaubsfilm");
        var paused = Item("handbuch.pdf", 14 * mb, "builtin:documents");
        var failed = Item("archiv-2025.7z", 80 * mb, "builtin:archives");
        var done = Item("lied.flac", 38 * mb, "builtin:music");
        ui.Downloads.Add(new[] { running, one, two, paused, failed, done }, start: false);

        long now = Environment.TickCount64;
        foreach (var (item, share) in new[] { (running, 0.42), (one, 0.8) })
        {
            item.BeginRun();
            long total = item.TotalBytes, quarter = total / 4;
            long part = (long)(quarter * share);
            var segments = new[] { part, part / 2, part, part / 3 };
            ((IProgress<DownloadProgress>)item).Report(new DownloadProgress(total, segments.Select(s => s - s / 60).Sum(), 0, segments.Select(s => s - s / 60).ToArray(),
                new[] { quarter, quarter, quarter, total - 3 * quarter }));
            item.Tick(now - 1000);
            ((IProgress<DownloadProgress>)item).Report(new DownloadProgress(total, segments.Sum(), 0, segments,
                new[] { quarter, quarter, quarter, total - 3 * quarter }));
            item.Tick(now);
        }
        two.Status = DownloadStatus.Queued;
        failed.Fail(new HttpRequestExceptionStub().Value);
        done.Status = DownloadStatus.Completed;
        done.CompletedAt = DateTime.Now;
        ui.Downloads.Stats.Push(18.4 * mb, 2, 1600 * mb);
        for (int i = 0; i < 200; i++) ui.Downloads.Stats.History.Add((12 + 6 * Math.Sin(i / 14.0)) * mb);
        ui.Downloads.Stats.FreeSpaceText = Loc.T("Stats.Free", Format.Bytes(412L * 1024 * mb));
        ui.Downloads.SetCategory(new[] { done }, "builtin:music"); // zählt die Seitenleiste neu
    }

    private sealed class HttpRequestExceptionStub
    {
        public Exception Value => new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound);
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    public async Task Screenshots(string theme)
    {
        if (Target is null) return;
        using var ui = new Ui(s =>
        {
            s.Theme = theme == "dark" ? ThemeMode.Dark : ThemeMode.Light;
            s.DownloadFolder = "/home/user/Downloads";
        });
        // Der Online-Check der Attrappe nennt den Dateinamen aus dem Link.
        ui.Rapidgator.Override = r => r.Endpoint != "file/check_link" ? null : FakeRapidgator.Ok("["
            + string.Join(",", r.Form["url"].Split(',').Select(link => System.Text.Json.JsonSerializer.Serialize(link.Contains("noaccess")
                ? new System.Collections.Generic.Dictionary<string, string> { ["url"] = link, ["status"] = "NO ACCESS" }
                : new System.Collections.Generic.Dictionary<string, string>
                {
                    ["url"] = link, ["status"] = "ACCESS", ["size"] = "524288000",
                    ["filename"] = link[(link.LastIndexOf('/') + 1)..^".html".Length],
                }))) + "]");
        ui.Window.Width = 960;
        ui.Window.Height = 600;
        Fill(ui);
        Save(ui, "main-" + theme);

        string noAccess = "https://rapidgator.net/file/" + new string('a', 32) + "/noaccess.bin.html";
        await ui.Downloads.Rapidgator.LoginAsync(FakeRapidgator.Login, FakeRapidgator.Password, null, default);
        Ui.Pump();
        ui.Window.OpenAdd(FakeRapidgator.FileUrl + "\n" + noAccess + "\nhttps://rapidgator.net/file/" + new string('b', 32)
            + "/Reise.part1.rar.html\nhttps://rapidgator.net/file/" + new string('c', 32) + "/Reise.part2.rar.html");
        var add = ui.Sheet<AddSheet>();
        await Ui.WaitUntil(() => add.Rows.OfType<AddCandidate>().All(c => c.Check is not (CheckStatus.None or CheckStatus.Checking)), "Check");
        await Task.Delay(500);
        Save(ui, "add-" + theme);
        ui.Window.CloseSheet();

        RapidgatorSheet.Open(ui.Window);
        Save(ui, "rapidgator-account-" + theme);
        ui.Window.CloseSheet();

        ui.Downloads.LogoutRapidgator();
        Ui.Pump();
        RapidgatorSheet.Open(ui.Window);
        Save(ui, "rapidgator-signin-" + theme);
        ui.Window.CloseSheet();

        SettingsSheet.Open(ui.Window);
        Save(ui, "settings-" + theme);
        ui.Window.CloseSheet();

        // Nur zum Ansehen beim Entwickeln: die übrigen Sheets (x-*), sie landen im selben Ordner.
        if (Environment.GetEnvironmentVariable("D6_SCREENSHOTS_ALL") is null) return;
        var item = ui.Downloads.Items[0];
        void Extra(string name, Action open)
        {
            open();
            Save(ui, "x-" + name + "-" + theme);
            ui.Window.CloseSheet();
        }
        Extra("detail", () => DetailSheet.Open(ui.Window, item));
        Extra("edit", () => EditSheet.Open(ui.Window, new() { ui.Downloads.Items[3] }));
        Extra("debrid", () => DebridSheet.Open(ui.Window));
        Extra("hosts", () => HostsSheet.Open(ui.Window));
        Extra("browser", () => BrowserSheet.Open(ui.Window));
        Extra("categories", () => CategoriesSheet.Open(ui.Window));
        ui.Downloads.UpdateQueues(new() { new QueueSetting { Id = "n", Name = "Nacht", Schedule = new ScheduleSetting() } });
        Extra("queues", () => QueuesSheet.Open(ui.Window));
        Extra("password", () => PasswordSheet.Open(ui.Window, new[] { ui.Downloads.Items[1] }));
        AfterSheet.Open(ui.Window);
        await ui.Sheet<AfterSheet>().Loaded;
        Save(ui, "x-after-" + theme);
        ui.Window.CloseSheet();

        ui.Window.Width = 700;
        ui.Window.Height = 480;
        Save(ui, "x-small-main-" + theme);
        Extra("small-add", () => ui.Window.OpenAdd("http://127.0.0.1:1/datei.bin"));
        Extra("small-settings", () => SettingsSheet.Open(ui.Window));
    }
}
