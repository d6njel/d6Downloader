using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using D6Downloader.Downloads;
using D6Downloader.Platform.Linux;
using D6Downloader.Services;

namespace D6Downloader;

public partial class App : Application
{
    /// <summary>Von <see cref="Program"/> vor dem Start gesetzt.</summary>
    internal static IpcInbox? Inbox;

    internal static bool Autostarted;

    private AppController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Ohne klassische Desktop-Laufzeit (Tests, Vorschau) baut der Aufrufer selbst, was er braucht.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) Start(desktop);
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Fortsetzungen nach await müssen auf den UI-Thread zurückkommen, der DownloadManager verlässt sich darauf.
        System.Threading.SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

        var keepAwake = new SystemdInhibitKeepAwake();
        // Als einzige Stelle bekommt der Manager die echten Aktionen für „Nach Abschluss“.
        var downloads = new DownloadManager(new AvaloniaTimerFactory(), new AvaloniaUiDispatcher(), new LinuxPowerActions(),
            keepAwake: keepAwake);
        downloads.BeginLoadSettings();
        downloads.AwaitSettings();
        // Die Systemaufrufe für „Nach Abschluss“ nie auf dem UI-Thread.
        downloads.AfterCompletion.Runner = System.Threading.Tasks.Task.Run;

        // Das Fenster kann ins Tray verschwinden: Beendet wird nur ausdrücklich.
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        var controller = _controller = new AppController(this, downloads, AppServices.CreateLinux(), desktop);
        desktop.ShutdownRequested += (_, _) => controller.PrepareQuit(); // Abmelden oder Herunterfahren
        desktop.Exit += (_, _) =>
        {
            controller.Dispose();
            keepAwake.Dispose();
        };

        // Beenden durch Signal (Abmelden, kill) soll den Stand genauso sichern.
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            _signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                Dispatcher.UIThread.Post(controller.Quit);
            }));

        controller.Start(Autostarted);
        Inbox?.Attach(message => Dispatcher.UIThread.Post(() => controller.OnMessage(message)));
    }

    private readonly System.Collections.Generic.List<PosixSignalRegistration> _signals = new();
}
