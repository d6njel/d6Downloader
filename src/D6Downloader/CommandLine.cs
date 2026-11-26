using System;
using System.Collections.Generic;
using System.IO;
using D6Downloader.Browser;
using D6Downloader.Platform.Linux;

namespace D6Downloader;

internal enum CommandKind { App, Version, Help, Add, HeadlessDownload, Invalid }

internal sealed record Command(CommandKind Kind, IReadOnlyList<string> Urls, string? OutDir = null, string? Problem = null);

/// <summary>
/// Die Befehlszeile. Nur der Start ohne Argumente (und der Autostart) öffnet die App: Ein Tippfehler soll nicht still ein
/// Fenster starten. Der Host-Start durch den Browser ist vorher abgefangen (<see cref="NativeHost.IsHostLaunch"/>).
/// </summary>
internal static class CommandLine
{
    public const int UsageError = 2;

    public const string Usage =
        "Aufruf:\n" +
        "  d6downloader                                    Programm starten\n" +
        "  d6downloader --add <url> [<url> ...]            Links an das Programm geben (startet es bei Bedarf)\n" +
        "  d6downloader --headless-download <url> --out <ordner>\n" +
        "                                                  eine Datei ohne Oberfläche und ohne Konten laden\n" +
        "  d6downloader --version | --help\n" +
        "  d6downloader --native-host                      Verbindung für die Browser-Erweiterung";

    private static readonly string[] NoUrls = Array.Empty<string>();

    public static Command Parse(string[] args)
    {
        if (args.Length == 0 || args is [XdgAutostart.Argument]) return new(CommandKind.App, NoUrls);
        switch (args[0])
        {
            case "--version" when args.Length == 1:
                return new(CommandKind.Version, NoUrls);
            case "--help" or "-h" when args.Length == 1:
                return new(CommandKind.Help, NoUrls);
            case "--add":
            {
                var urls = args[1..];
                if (urls.Length == 0) return Invalid("--add braucht mindestens eine Adresse.");
                foreach (string url in urls)
                    if (!IsWebAddress(url)) return Invalid("Keine http- oder https-Adresse: " + url);
                return new(CommandKind.Add, urls);
            }
            case "--headless-download":
                if (args.Length != 4 || args[2] != "--out" || args[3].Length == 0)
                    return Invalid("Erwartet: --headless-download <url> --out <ordner>");
                return IsWebAddress(args[1])
                    ? new(CommandKind.HeadlessDownload, new[] { args[1] }, args[3])
                    : Invalid("Keine http- oder https-Adresse: " + args[1]);
            default:
                return Invalid("Unbekannte Argumente: " + string.Join(' ', args));
        }
    }

    private static Command Invalid(string problem) => new(CommandKind.Invalid, NoUrls, Problem: problem);

    private static bool IsWebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && Downloads.LinkParser.ContainsLink(text);

    /// <summary>Gibt die Links an die laufende App, sonst wird sie losgelöst vom Terminal gestartet.</summary>
    public static int Add(IReadOnlyList<string> urls, IAppLink link, TimeSpan launchTimeout, TextWriter errors)
    {
        if (NativeHost.Deliver(string.Join('\n', urls), link, launchTimeout)) return 0;
        errors.WriteLine("d6downloader: Das Programm ist nicht erreichbar, die Links wurden nicht übergeben.");
        return 1;
    }
}
