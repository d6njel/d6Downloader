using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using D6Downloader.Browser;
using D6Downloader.Downloads;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Platform;
using D6Downloader.Power;
using D6Downloader.Services;
using D6Downloader.Tests;
using Xunit;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(D6Downloader.UiTests.TestAppBuilder))]
// Die Dienste, die der Core statisch hält (Sprache, Schlüssel), und ein UI-Thread: alles nacheinander.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace D6Downloader.UiTests;

public static class TestAppBuilder
{
    /// <summary>
    /// Netz-Sperre: Alles außer dem eigenen Rechner läuft in einen toten Proxy. So erreicht auch ein Versehen in einem Test nie
    /// einen echten Server.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void BlockTheNetwork()
    {
        foreach (string name in new[] { "http_proxy", "https_proxy", "HTTPS_PROXY", "ALL_PROXY", "all_proxy" })
            Environment.SetEnvironmentVariable(name, "http://127.0.0.1:1");
        Environment.SetEnvironmentVariable("no_proxy", "127.0.0.1,localhost");
        Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost");
    }

    /// <summary>Die echte App mit ihren Stilen, gezeichnet in den Speicher: Es erscheint nie ein Fenster.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class FakeShell : IShell
{
    public readonly List<string> Opened = new();
    public void Open(string pathOrUrl) => Opened.Add(pathOrUrl);
    public void ShowInFolder(string path) => Opened.Add(path);
}

internal sealed class FakeAutostart : IAutostart
{
    public bool Enabled;
    public int Writes;
    public bool IsEnabled() => Enabled;
    public void Set(bool enabled)
    {
        Enabled = enabled;
        Writes++;
    }
}

internal sealed class FakeNotifier : INotifier
{
    public readonly List<string> Titles = new();
    public void Notify(string title, string body, bool error = false, Action? onClick = null) => Titles.Add(title);
    public Task<bool> HasTrayAsync() => Task.FromResult(false);
    public void Dispose() { }
}

internal sealed class FakeLocations : INativeHostLocations
{
    private readonly string _chrome;
    public FakeLocations(string root) => _chrome = Path.Combine(root, "chrome", "NativeMessagingHosts");
    public IReadOnlyList<string> ManifestDirectories(BrowserKind kind) =>
        kind == BrowserKind.Chrome ? new[] { _chrome } : Array.Empty<string>();
}

/// <summary>Würde nie etwas ausschalten.</summary>
internal sealed class FakePower : IPowerActions
{
    public readonly List<PowerAction> Executed = new();
    public bool IsAvailable(PowerAction action) => action is PowerAction.Shutdown or PowerAction.Sleep;
    public string? Execute(PowerAction action)
    {
        Executed.Add(action);
        return null;
    }
}

/// <summary>Das Hauptfenster mit echtem Manager, Rapidgator gegen die Attrappe auf 127.0.0.1, alles andere ohne Wirkung.</summary>
internal sealed class Ui : IDisposable
{
    public readonly TempDir Dir = new();
    public readonly FakeRapidgator Rapidgator = new();
    public readonly FakeShell Shell = new();
    public readonly FakeAutostart Autostart = new();
    public readonly FakeNotifier Notifier = new();
    public readonly FakePower Power = new();
    public readonly DownloadManager Downloads;
    public readonly AppController App;

    public Ui(Action<AppSettings>? configure = null)
    {
        Downloads = new DownloadManager(new AvaloniaTimerFactory(), new AvaloniaUiDispatcher(), Power,
            rapidgator: new RapidgatorOptions
            {
                BaseUri = Rapidgator.BaseUri,
                RetryPause = TimeSpan.FromMilliseconds(10),
                AllowDownloadHost = uri => uri.Host == "127.0.0.1",
                PreferHttps = false,
            });
        Downloads.Settings.DownloadFolder = Dir.Path;
        Downloads.Settings.StartImmediately = false;
        configure?.Invoke(Downloads.Settings);
        var services = new AppServices(Shell, Autostart, new BrowserSetup(new FakeLocations(Dir.Path), "/opt/d6downloader"),
            Notifier, Dir.Path);
        App = new AppController(Avalonia.Application.Current!, Downloads, services);
        Window.Show();
        Pump();
    }

    public MainWindow Window => App.Window;

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task WaitUntil(Func<bool> done, string what = "")
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "Zeitüberschreitung " + what);
            await Task.Delay(20);
            Pump();
        }
    }

    public T Sheet<T>() where T : Control
    {
        Pump();
        return Assert.IsType<T>(Window.CurrentSheet);
    }

    public static T Find<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException("fehlt: " + name);

    public static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyVisible, "unsichtbar: " + button.Name);
        Assert.True(button.IsEffectivelyEnabled, "gesperrt: " + button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    public static void Click(Control root, string name) => Click(Find<Button>(root, name));

    /// <summary>Alle sichtbaren Texte unter einem Element.</summary>
    public static List<string> Texts(Visual root) => root.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!).ToList();

    public void Dispose()
    {
        if (Window.IsSheetOpen) Window.CloseSheet();
        App.Dispose();
        Window.Close();
        Pump();
        Rapidgator.Dispose();
        Dir.Dispose();
    }
}
