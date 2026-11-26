using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using D6Downloader.Browser;
using D6Downloader.Platform.Linux;
using D6Downloader.Services;

namespace D6Downloader;

/// <summary>
/// Einstieg. Startet der Browser das Programm als Native-Messaging-Host, läuft nur die kurze Host-Schleife: ohne Avalonia, ohne
/// Fenster. Sonst gibt es genau eine Instanz; ein zweiter Start holt die erste nach vorn.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Zuerst der Host-Start: Die Browser übergeben eigene Argumente.
        if (NativeHost.IsHostLaunch(args))
        {
            LinuxPlatform.Register();
            return NativeHost.RunStandard(new UnixSocketAppLink());
        }

        var command = CommandLine.Parse(args);
        switch (command.Kind)
        {
            case CommandKind.App:
                return RunApp(args);
            case CommandKind.Version:
                Console.Out.WriteLine("d6downloader " + NativeHost.AppVersion());
                return 0;
            case CommandKind.Help:
                Console.Out.WriteLine(CommandLine.Usage);
                return 0;
            case CommandKind.Add:
                LinuxPlatform.Register();
                return CommandLine.Add(command.Urls, new UnixSocketAppLink(), NativeHost.DefaultLaunchTimeout, Console.Error);
            case CommandKind.HeadlessDownload:
                LinuxPlatform.Register();
                return HeadlessDownload.RunStandard(command.Urls[0], command.OutDir!);
            default:
                Console.Error.WriteLine("d6downloader: " + command.Problem);
                Console.Error.WriteLine(CommandLine.Usage);
                return CommandLine.UsageError;
        }
    }

    // Eigene Methode, damit Avalonia nur geladen wird, wenn die App wirklich startet.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(string[] args)
    {
        LinuxPlatform.Register();
        bool autostart = args.Contains(XdgAutostart.Argument);

        // Eine Instanz reicht: zwei würden dieselben Dateien beschreiben.
        var paths = RuntimePaths.Resolve();
        SingleInstanceLock? instance;
        try
        {
            instance = SingleInstanceLock.TryAcquire(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("d6downloader: " + ex.Message);
            return 1;
        }
        if (instance is null)
        {
            if (!autostart) IpcClient.TryShow(paths);
            return 0;
        }

        using (instance)
        {
            // Der Socket steht, bevor die Oberfläche da ist: Was in der Zwischenzeit ankommt, wartet in der Ablage.
            var inbox = new IpcInbox();
            IpcServer? server = null;
            try
            {
                server = IpcServer.Create(instance);
                server.ShowRequested += inbox.Show;
                server.LinksReceived += inbox.Links;
                server.Listen();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                // Ohne Socket läuft die App, nur Browser und ein zweiter Start erreichen sie nicht.
                Console.Error.WriteLine("d6downloader: " + ex.Message);
            }

            using (server)
            {
                App.Inbox = inbox;
                App.Autostarted = autostart;
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "d6downloader" })
        .LogToTrace();
}
