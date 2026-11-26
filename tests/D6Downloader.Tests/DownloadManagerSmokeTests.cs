using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
