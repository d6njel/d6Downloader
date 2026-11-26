using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using D6Downloader.Downloads;
using D6Downloader.Packages;
using D6Downloader.Platform;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Der Manager läuft ohne Oberfläche: Timer, UI-Thread und Wachhalten sind Attrappen.</summary>
public class DownloadManagerSmokeTests
{
    private sealed class FakeTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeTimers : ITimerFactory
    {
        public readonly List<FakeTimer> All = new();

        public IUiTimer Create(TimeSpan interval, bool urgent = false)
        {
            var timer = new FakeTimer { Interval = interval };
            All.Add(timer);
            return timer;
        }
    }

    /// <summary>Ein "UI-Thread": Alles Eingereihte läuft im Test-Thread.</summary>
    private sealed class QueueContext : SynchronizationContext, IUiDispatcher
    {
        private readonly BlockingCollection<Action> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add(() => d(state));
        public void Post(Action action) => _queue.Add(action);

        public void RunUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!done())
            {
                Assert.True(DateTime.UtcNow < until, "Zeitüberschreitung");
                if (_queue.TryTake(out var action, 20)) action();
            }
        }
    }

    private sealed class RecordingKeepAwake : IKeepAwake
    {
        public readonly List<bool> Calls = new();
        public void Set(bool on) => Calls.Add(on);
    }

    [Fact]
    public void A_download_runs_to_the_end_and_keeps_the_system_awake_only_while_running()
    {
        using var server = new TestServer(200_000);
        using var dir = new TempDir();
        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var timers = new FakeTimers();
            var keepAwake = new RecordingKeepAwake();
            var manager = new DownloadManager(timers, ui, keepAwake: keepAwake);
            var item = new DownloadItem(server.Url, "m.bin", dir.Path);

            manager.Add(new[] { item }, start: true);
            Assert.True(keepAwake.Calls.Last());
            Assert.Contains(timers.All, t => t.IsEnabled);

            ui.RunUntil(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed);

            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.False(keepAwake.Calls.Last());
            Assert.Equal(server.Data, File.ReadAllBytes(dir.File("m.bin")));
            manager.Shutdown();
            Assert.DoesNotContain(timers.All, t => t.IsEnabled);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class FakeTrash : ITrash
    {
        public readonly List<string> Moved = new();

        public void MoveToTrash(string path)
        {
            Moved.Add(path);
            File.Delete(path); // nur im Testordner, steht für „weg im Papierkorb“
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Extracted_archives_go_to_the_trash_only_when_wanted_and_successful(bool option, bool broken)
    {
        using var dir = new TempDir();
        string archive = dir.File("a.zip");
        if (broken) File.WriteAllText(archive, "kein ZIP");
        else
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("inhalt.txt").Open());
            writer.Write("hallo");
        }
        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var trash = new FakeTrash();
            var manager = new DownloadManager(new FakeTimers(), ui, tools: NoToolLocator.Instance, trash: trash);
            manager.Settings.DeleteArchivesAfterExtract = option;
            var item = new DownloadItem("http://127.0.0.1:1/a.zip", "a.zip", dir.Path);
            manager.Add(new[] { item }, start: false);
            item.Status = DownloadStatus.Completed;
            item.FinalPath = archive;

            manager.ExtractNow(new[] { item });
            ui.RunUntil(() => item.Extract is not (ExtractState.None or ExtractState.Extracting));

            bool expected = option && !broken;
            Assert.Equal(broken ? ExtractState.Failed : ExtractState.Done, item.Extract);
            Assert.Equal(expected ? new[] { archive } : Array.Empty<string>(), trash.Moved);
            Assert.Equal(!expected, File.Exists(archive));
            Assert.Equal(expected, item.Log.Any(l => l.Text == Loc.T("Extract.Log.ArchivesTrashed", 1)));
            Assert.Equal(expected ? "trashed" : null, broken ? null : item.ExtractDetail);
            if (!broken) Assert.True(File.Exists(Path.Combine(ExtractPlanner.SingleTargetDir(archive), "inhalt.txt")));
            manager.Shutdown();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void Archives_are_deleted_permanently_only_with_both_options_and_success(bool afterExtract, bool permanently, bool broken)
    {
        using var dir = new TempDir();
        string archive = dir.File("a.zip");
        if (broken) File.WriteAllText(archive, "kein ZIP");
        else WriteZip(archive, "inhalt.txt");
        bool expected = afterExtract && permanently && !broken;

        Extract(dir, archive, afterExtract, permanently, (item, trash) =>
        {
            Assert.Equal(broken ? ExtractState.Failed : ExtractState.Done, item.Extract);
            Assert.Empty(trash.Moved);
            Assert.Equal(!expected, File.Exists(archive));
            Assert.Equal(expected, item.Log.Any(l => l.Text == Loc.T("Extract.Log.ArchivesDeleted", 1)));
            if (!broken) Assert.Equal(expected ? "deleted" : null, item.ExtractDetail);
        });
    }

    /// <summary>Mit den echten Entpackern des Rechners: Keiner darf das beschädigte Archiv als entpackt durchwinken.</summary>
    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void A_zip_with_a_flipped_byte_is_never_deleted(CompressionLevel level)
    {
        using var dir = new TempDir();
        string archive = dir.File("a.zip");
        var data = new byte[4000];
        new Random(3).NextBytes(data);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("x.bin", level).Open();
            s.Write(data);
        }
        var bytes = File.ReadAllBytes(archive);
        bytes[2000] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var manager = new DownloadManager(new FakeTimers(), ui, trash: new FakeTrash());
            manager.Settings.DeleteArchivesAfterExtract = true;
            manager.Settings.DeleteArchivesPermanently = true;
            var item = new DownloadItem("http://127.0.0.1:1/a.zip", "a.zip", dir.Path);
            manager.Add(new[] { item }, start: false);
            item.Status = DownloadStatus.Completed;
            item.FinalPath = archive;

            manager.ExtractNow(new[] { item });
            ui.RunUntil(() => item.Extract is not (ExtractState.None or ExtractState.Extracting));

            Assert.Equal(ExtractState.Failed, item.Extract);
            Assert.Equal(bytes, File.ReadAllBytes(archive));
            Assert.False(Directory.Exists(ExtractPlanner.SingleTargetDir(archive)));
            manager.Shutdown();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Braucht <c>unar</c>, sonst prüft der Test nichts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rar_without_end_block_is_extracted_but_kept_with_its_own_log_line(bool permanently)
    {
        var env = ToolSearchEnv.Current(Array.Empty<string>());
        if (ToolLocator.FindUnar(env) is null || ToolLocator.FindLsar(env) is null) return;
        using var dir = new TempDir();
        string archive = dir.File("a.rar");
        File.WriteAllBytes(archive, RarSamples.Several(4, new[] { ("x.txt", new byte[] { 1, 2, 3 }) }, out _, endBlock: false));

        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var trash = new FakeTrash();
            var manager = new DownloadManager(new FakeTimers(), ui, trash: trash);
            manager.Settings.DeleteArchivesAfterExtract = true;
            manager.Settings.DeleteArchivesPermanently = permanently;
            var item = new DownloadItem("http://127.0.0.1:1/a.rar", "a.rar", dir.Path);
            manager.Add(new[] { item }, start: false);
            item.Status = DownloadStatus.Completed;
            item.FinalPath = archive;

            manager.ExtractNow(new[] { item });
            ui.RunUntil(() => item.Extract is not (ExtractState.None or ExtractState.Extracting));

            Assert.Equal(ExtractState.Done, item.Extract);
            Assert.Null(item.ExtractDetail);
            Assert.True(File.Exists(archive));
            Assert.Empty(trash.Moved);
            Assert.Contains(item.Log, l => l.Text == Loc.T("Extract.Log.ArchivesKeptUnverified", 1));
            manager.Shutdown();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Gegen ein echtes 7-Zip; ohne eines prüft der Test nichts.</summary>
    [Fact]
    public void A_missing_or_wrong_password_deletes_nothing_and_the_right_one_deletes_all_parts()
    {
        if (ToolLocator.FindSevenZip(null, ToolSearchEnv.Current(Array.Empty<string>())) is not { } sevenZip) return;
        using var dir = new TempDir();
        using var source = new TempDir();
        var data = new byte[20_000];
        new Random(1).NextBytes(data); // nicht komprimierbar: mehrere Teile
        File.WriteAllBytes(source.File("data.bin"), data);
        var start = new ProcessStartInfo(sevenZip) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (string a in new[] { "a", "-pRichtig 1", "-v6k", dir.File("pack.7z"), source.File("data.bin") }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        string[] parts = Directory.GetFiles(dir.Path, "pack.7z.*").Order().ToArray();
        Assert.True(parts.Length > 1);

        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var trash = new FakeTrash();
            var manager = new DownloadManager(new FakeTimers(), ui, trash: trash);
            manager.Settings.DeleteArchivesAfterExtract = true;
            manager.Settings.DeleteArchivesPermanently = true;
            manager.Settings.ArchivePasswords.Passwords = new();
            var items = parts.Select(p => new DownloadItem("http://127.0.0.1:1/" + Path.GetFileName(p), Path.GetFileName(p), dir.Path)
            { PackageId = "pack", PackageName = "pack" }).ToList();
            manager.Add(items, start: false);
            for (int i = 0; i < parts.Length; i++)
            {
                items[i].Status = DownloadStatus.Completed;
                items[i].FinalPath = parts[i];
            }
            void Wait() => ui.RunUntil(() => items[0].Extract is not (ExtractState.None or ExtractState.Extracting));

            manager.ExtractNow(items);
            Wait();
            Assert.Equal(ExtractState.NeedPassword, items[0].Extract);
            Assert.All(parts, p => Assert.True(File.Exists(p)));

            manager.EnterPassword(items, "falsch", remember: false);
            Wait();
            Assert.Equal(ExtractState.NeedPassword, items[0].Extract);
            Assert.Equal("1", items[0].ExtractDetail); // das falsche wurde wirklich probiert
            Assert.All(parts, p => Assert.True(File.Exists(p)));
            Assert.False(File.Exists(dir.File("data.bin")));

            manager.EnterPassword(items, "Richtig 1", remember: false);
            Wait();
            Assert.Equal(ExtractState.Done, items[0].Extract);
            Assert.Equal("deleted", items[0].ExtractDetail);
            Assert.Equal(data, File.ReadAllBytes(dir.File("data.bin")));
            Assert.All(parts, p => Assert.False(File.Exists(p)));
            Assert.Empty(trash.Moved);
            manager.Shutdown();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void WriteZip(string path, string entry)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
        writer.Write("hallo");
    }

    private static void Extract(TempDir dir, string archive, bool afterExtract, bool permanently,
        Action<DownloadItem, FakeTrash> check)
    {
        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var trash = new FakeTrash();
            var manager = new DownloadManager(new FakeTimers(), ui, tools: NoToolLocator.Instance, trash: trash);
            manager.Settings.DeleteArchivesAfterExtract = afterExtract;
            manager.Settings.DeleteArchivesPermanently = permanently;
            var item = new DownloadItem("http://127.0.0.1:1/a.zip", "a.zip", dir.Path);
            manager.Add(new[] { item }, start: false);
            item.Status = DownloadStatus.Completed;
            item.FinalPath = archive;

            manager.ExtractNow(new[] { item });
            ui.RunUntil(() => item.Extract is not (ExtractState.None or ExtractState.Extracting));

            check(item, trash);
            manager.Shutdown();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Ein als „beschädigt“ gemerktes RAR lässt sich erneut entpacken, über die echten Linux-Werkzeuge (ohne unar prüft der Test nichts).</summary>
    [Fact]
    public void A_failed_rar_can_be_extracted_again()
    {
        var env = ToolSearchEnv.Current(Array.Empty<string>());
        if (ToolLocator.FindUnar(env) is null || ToolLocator.FindLsar(env) is null) return;
        using var dir = new TempDir();
        string archive = RarSamples.Fixture("rar5_stored", dir.Path);
        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var manager = new DownloadManager(new FakeTimers(), ui);
            var item = new DownloadItem("http://127.0.0.1:1/a.rar", Path.GetFileName(archive), dir.Path);
            manager.Add(new[] { item }, start: false);
            item.Status = DownloadStatus.Completed;
            item.FinalPath = archive;
            foreach (var state in new[] { ExtractState.NeedRarTool, ExtractState.Failed })
            {
                item.SetExtract(state, state == ExtractState.Failed ? ExtractFailure.Corrupt : ExtractFailure.None);
                Assert.True(manager.CanExtract(item));
            }

            manager.ExtractNow(new[] { item });
            ui.RunUntil(() => item.Extract is not (ExtractState.Extracting or ExtractState.Failed));

            Assert.Equal(ExtractState.Done, item.Extract);
            Assert.True(File.Exists(Path.Combine(ExtractPlanner.SingleTargetDir(archive), "helloworld.txt")));
            manager.Shutdown();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
