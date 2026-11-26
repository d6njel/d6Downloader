using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using D6Downloader.Browser;
using D6Downloader.Downloads;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Power;
using D6Downloader.Services;

namespace D6Downloader;

/// <summary>
/// Verbindet Manager, Fenster und die Dienste drumherum: Tray, Benachrichtigungen, Zwischenablage, Click'n'Load, Aussehen und
/// Beenden. Ohne <see cref="Start"/> tut sie nichts nach außen (so benutzen es die Tests).
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _tray;
    private bool _trayHosted;
    private DispatcherTimer? _clipboardTimer;
    private ClipboardWatcher? _clipboardWatcher;
    private string? _clipboardText;
    private bool _clipboardReading;
    private ClickNLoadServer? _clickNLoad;
    private RapidgatorState _rapidgatorState;
    private bool _disposed;

    public AppController(Application application, DownloadManager downloads, AppServices services,
        IClassicDesktopStyleApplicationLifetime? desktop = null)
    {
        _application = application;
        _desktop = desktop;
        Downloads = downloads;
        Services = services;
        ApplyTheme();
        Window = new MainWindow(this);
        Window.ApplyScale(downloads.Settings.UiScaleFactor);
    }

    public DownloadManager Downloads { get; }
    public AppServices Services { get; }
    public MainWindow Window { get; }

    /// <summary>Gesetzt, wenn wirklich beendet wird: Das Fenster geht dann nicht mehr nur ins Tray.</summary>
    public bool Quitting { get; private set; }

    public void Start(bool autostarted)
    {
        Downloads.ItemFinished += OnItemFinished;
        Downloads.AfterCompletion.CountdownStarted += OnCountdownStarted;
        _rapidgatorState = Downloads.Rapidgator.State;
        Downloads.RapidgatorChanged += OnRapidgatorChanged;

        if (!autostarted) Show();

        // Liste und Tray erst nach dem ersten Fenster.
        Dispatcher.UIThread.Post(async () => await Downloads.LoadAsync(), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(async () => await StartBackgroundAsync(autostarted), DispatcherPriority.Background);
    }

    private async Task StartBackgroundAsync(bool autostarted)
    {
        CreateTray();
        Downloads.StartFreeSpace();
        Window.StartNotices();
        UpdateClipboardWatch();
        if (Downloads.Settings.ClickNLoad && StartClickNLoad() is { } problem)
            Services.Notifier.Notify(Loc.T("ClickNLoad.Title"), problem, error: true);

        _trayHosted = await Services.Notifier.HasTrayAsync();
        // Beim Anmelden bleibt das Fenster zu. Gibt es keinen Tray, wäre die App unsichtbar: dann doch zeigen.
        if (autostarted && !CanHideToTray && !Quitting) Show();
    }

    private void Show()
    {
        if (_desktop is not null) _desktop.MainWindow ??= Window;
        Window.Reveal();
    }

    /// <summary>Eine Nachricht vom Socket: null = Fenster zeigen, sonst Text mit Links.</summary>
    public void OnMessage(string? links)
    {
        if (_disposed || Quitting) return;
        if (links is null) Show();
        else Window.ReceiveLinks(links);
    }

    // ---------------------------------------------------------------- Tray und Meldungen

    private void CreateTray()
    {
        if (_tray is not null) return;
        var open = new NativeMenuItem(Loc.T("Tray.Open"));
        open.Click += (_, _) => Show();
        var exit = new NativeMenuItem(Loc.T("Tray.Exit"));
        exit.Click += (_, _) => Quit();
        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(exit);
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://d6downloader/Assets/tray.png"))),
            ToolTipText = "d6Downloader",
            Menu = menu,
        };
        _tray.Clicked += (_, _) => Show();
        TrayIcon.SetIcons(_application, new TrayIcons { _tray });
    }

    /// <summary>Ob das Fenster beim Schließen ins Tray gehen kann: nur, wenn das Symbol dort auch angezeigt wird.</summary>
    public bool CanHideToTray => _tray is not null && _trayHosted;

    private bool WindowActive => Window is { IsVisible: true, IsActive: true };

    /// <summary>Der Countdown vor Herunterfahren oder Ruhezustand beginnt: zusätzlich als Meldung.</summary>
    private void OnCountdownStarted(object? sender, EventArgs e)
    {
        var plan = Downloads.AfterCompletion;
        Services.Notifier.Notify(Loc.T("Countdown.Title", PowerText.Name(plan.Kind)),
            Loc.T("Countdown.TrayText", PowerText.Sentence(plan.Kind)));
    }

    /// <summary>Meldet fertige und fehlgeschlagene Downloads, solange das Fenster nicht im Vordergrund ist.</summary>
    private void OnItemFinished(DownloadItem item, int batch)
    {
        if (WindowActive) return;
        var s = Downloads.Settings;
        bool summary = batch > 1 && s.NotifyAllDone;
        if (item.Status == DownloadStatus.Failed && s.NotifyFailed)
            Services.Notifier.Notify(Loc.T("Notify.Failed"), $"{item.FileName}\n{item.Error}", error: true);
        else if (summary)
            Services.Notifier.Notify(Loc.T("Notify.AllDone"), Loc.Plural("Notify.AllDoneCount", batch));
        else if (item.Status == DownloadStatus.Completed && s.NotifyCompleted)
            Services.Notifier.Notify(Loc.T("Notify.Completed"), $"{item.FileName} · {Format.Bytes(item.TotalBytes)}");
    }

    private void OnRapidgatorChanged(object? sender, EventArgs e)
    {
        var state = Downloads.Rapidgator.State;
        if (state == _rapidgatorState) return;
        _rapidgatorState = state;
        if (state is RapidgatorState.CodeRequired or RapidgatorState.LoginFailed && !WindowActive)
            Services.Notifier.Notify(Loc.T("Rapidgator.Title"), Loc.T("Rapidgator.Attention"), error: true,
                onClick: () => Dispatcher.UIThread.Post(Show));
    }

    // ---------------------------------------------------------------- Zwischenablage

    private sealed class CachedClipboard : IClipboardReader
    {
        private readonly AppController _owner;
        public CachedClipboard(AppController owner) => _owner = owner;
        public string? ReadText() => _owner._clipboardText;
    }

    /// <summary>
    /// Überwachung der Zwischenablage nach der Einstellung. Linux meldet Änderungen nicht zuverlässig, deshalb wird einmal je
    /// Sekunde nachgesehen, nur solange sie eingeschaltet ist.
    /// </summary>
    public void UpdateClipboardWatch()
    {
        if (!Downloads.Settings.WatchClipboard)
        {
            _clipboardTimer?.Stop();
            _clipboardText = null;
            return;
        }
        _clipboardWatcher ??= new ClipboardWatcher(new CachedClipboard(this),
            uri => Downloads.Router.Supports(uri) == true,
            name => Downloads.Settings.CategoryFor(name) is not null,
            () => WindowActive,
            match => Services.Notifier.Notify(Loc.T("Clipboard.Title"), Loc.Plural("Clipboard.Hint", match.Count),
                onClick: () => Dispatcher.UIThread.Post(() => Window.ReceiveLinks(match.Text))));
        if (_clipboardTimer is null)
        {
            _clipboardTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _clipboardTimer.Tick += async (_, _) => await PollClipboardAsync();
        }
        _clipboardTimer.Start();
    }

    private async Task PollClipboardAsync()
    {
        if (_clipboardReading || Window.Clipboard is not { } clipboard) return;
        _clipboardReading = true;
        try
        {
            string? text = await clipboard.TryGetTextAsync();
            if (text == _clipboardText) return;
            _clipboardText = text;
            _clipboardWatcher?.OnClipboardChanged();
        }
        catch (Exception)
        {
            // Zwischenablage gerade nicht zu lesen
        }
        finally
        {
            _clipboardReading = false;
        }
    }

    // ---------------------------------------------------------------- Click'n'Load

    /// <summary>Warum der Server beim letzten Versuch nicht startete, null wenn er läuft oder nicht gewollt ist.</summary>
    public string? ClickNLoadError { get; private set; }

    /// <summary>
    /// Startet den Click'n'Load-Server auf 127.0.0.1, wenn er nicht schon läuft. Was ankommt, öffnet nur den Hinzufügen-Dialog.
    /// </summary>
    /// <returns>Null bei Erfolg, sonst die Meldung.</returns>
    public string? StartClickNLoad()
    {
        if (_clickNLoad is not null) return null;
        try
        {
            var server = ClickNLoadServer.Start();
            server.LinksReceived += s => Dispatcher.UIThread.Post(() => Window.ReceiveLinks(s.Text, s.Passwords));
            _clickNLoad = server;
            ClickNLoadError = null;
        }
        catch (SocketException ex)
        {
            ClickNLoadError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? Loc.T("ClickNLoad.PortBusy", ClickNLoadServer.DefaultPort)
                : Loc.T("ClickNLoad.Failed", ex.Message);
        }
        return ClickNLoadError;
    }

    public void StopClickNLoad()
    {
        _clickNLoad?.Dispose();
        _clickNLoad = null;
        ClickNLoadError = null;
    }

    // ---------------------------------------------------------------- Aussehen

    private void ApplyTheme() => _application.RequestedThemeVariant = Downloads.Settings.Theme switch
    {
        ThemeMode.Light => ThemeVariant.Light,
        ThemeMode.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default, // folgt der Arbeitsumgebung
    };

    /// <summary>Theme und Skalierung aus den Einstellungen übernehmen.</summary>
    public void ApplyAppearance()
    {
        ApplyTheme();
        Window.ApplyScale(Downloads.Settings.UiScaleFactor);
    }

    // ---------------------------------------------------------------- Beenden

    public void PrepareQuit() => Quitting = true;

    /// <summary>Beendet wirklich, auch wenn das Schließen des Fensters sonst nur ins Tray ginge.</summary>
    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        _desktop?.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Quitting = true;
        _clipboardTimer?.Stop();
        Downloads.RapidgatorChanged -= OnRapidgatorChanged;
        StopClickNLoad();
        Downloads.Shutdown();
        _tray?.Dispose();
        Services.Notifier.Dispose();
    }
}
