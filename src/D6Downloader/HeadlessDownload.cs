using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Downloads;
using D6Downloader.Engine;
using D6Downloader.Hosters.Rapidgator;

namespace D6Downloader;

/// <summary>
/// <c>--headless-download</c>: eine Datei mit der Engine laden, ohne Oberfläche, Einstellungen und Konten, vor allem für
/// Abnahmetests. Es wird nie etwas Vorhandenes angefasst: Ist der Name belegt (Datei, <c>.part</c> oder <c>.part.json</c>),
/// bekommt der Download eine Nummer. Gegen eine App, die gleichzeitig auf dasselbe Ziel schreibt, schützt die Reservierung der
/// Engine.
/// </summary>
internal static class HeadlessDownload
{
    public static int RunStandard(string url, string outDir)
    {
        using var stop = new CancellationTokenSource();
        void Cancel(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Cancel);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Cancel);
        return RunAsync(url, outDir, Console.Out, Console.Error, stop.Token).GetAwaiter().GetResult();
    }

    /// <summary>0: fertig, der Pfad steht auf <paramref name="output"/>. 1: gescheitert, der Grund auf <paramref name="errors"/>.</summary>
    public static async Task<int> RunAsync(string url, string outDir, TextWriter output, TextWriter errors, CancellationToken ct)
    {
        if (RapidgatorLinks.IsRapidgator(url))
        {
            errors.WriteLine("d6downloader: Rapidgator-Links gehen nur im Programm (dort ist die Anmeldung).");
            return 1;
        }
        try
        {
            string folder = Path.GetFullPath(outDir);
            using var engine = new HttpDownloader(new DownloadOptions());
            var info = await engine.ProbeAsync(url, ct).ConfigureAwait(false);
            string target = FreeName(folder, info.FileName);
            var result = await engine.DownloadAsync(new DownloadRequest(url, target), null, ct).ConfigureAwait(false);
            output.WriteLine(result.Path);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errors.WriteLine("d6downloader: abgebrochen.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException
            or InvalidOperationException or NotSupportedException or ArgumentException or UriFormatException)
        {
            errors.WriteLine("d6downloader: " + ErrorText.Describe(ex));
            return 1;
        }
    }

    /// <summary>Der erste Name, unter dem weder die Datei noch Teildaten liegen.</summary>
    private static string FreeName(string folder, string fileName)
    {
        // Der Name kommt vom Server: nie aus dem Zielordner hinaus.
        string name = FileNames.Sanitize(Path.GetFileName(fileName));
        if (name is "." or "..") name = "download";
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int i = 0; i < 10_000; i++)
        {
            string path = Path.Combine(folder, i == 0 ? name : $"{stem} ({i}){extension}");
            if (!Taken(path) && !Taken(path + ".part") && !Taken(path + ".part.json")) return path;
        }
        throw new IOException("Kein freier Dateiname: " + name);
    }

    private static bool Taken(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;
}
