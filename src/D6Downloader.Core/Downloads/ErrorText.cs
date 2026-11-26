using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using D6Downloader.Debrid;
using D6Downloader.Engine;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Platform;

namespace D6Downloader.Downloads;

/// <summary>
/// Kurze Fehlertexte für die Liste, in der Sprache der Oberfläche. Die Meldungen der Engine-Ausnahmen sind Entwicklertext:
/// Die Anzeige baut ihren Text aus den Angaben der Ausnahme.
/// </summary>
public static class ErrorText
{
    public static string Describe(Exception ex) => ex switch
    {
        ServerBusyException busy => Loc.T("Error.ServerBusy", (int?)busy.StatusCode ?? 0,
            Math.Max(0, Math.Round(busy.RetryAfter.TotalSeconds))),
        HttpRequestException { StatusCode: { } code } => Status(code),
        HttpRequestException { InnerException: AuthenticationException } => Loc.T("Error.SslInvalid"),
        HttpRequestException h => Socket(h) ?? Loc.T("Error.ConnectionFailed"),
        TimeoutException => Loc.T("Error.Timeout"),
        UriFormatException => Loc.T("Error.InvalidAddress"),
        StorageException { NeededBytes: >= 0 } disk => Loc.T("Error.DiskFull", disk.NeededBytes / (1024 * 1024), disk.FreeBytes / (1024 * 1024)),
        StorageException { InnerException: { } inner } => LocalFile(inner) ?? Loc.T("Error.Storage", inner.Message),
        RemoteFileChangedException => Loc.T("Error.FileChanged"),
        ChecksumMismatchException sum => Loc.T("Error.Checksum", sum.Expected, sum.Actual),
        DestinationExistsException exists => Loc.T("Error.DestinationExists", exists.Path),
        ServerProtocolException protocol => Protocol(protocol),
        PartialKeptException { Conflict: ResumeConflict.SizeChanged, RemoteSize: >= 0, LocalSize: >= 0 } size =>
            Loc.T("Error.Kept.SizeChanged", Format.Bytes(size.RemoteSize), Format.Bytes(size.LocalSize)),
        PartialKeptException { Conflict: ResumeConflict.NoRanges } => Loc.T("Error.Kept.NoRanges"),
        PartialKeptException => Loc.T("Error.Kept.FileChanged"),
        HtmlResponseException { HostsUnavailable: true } => Loc.T("Error.HtmlResponse.NoHosts"),
        HtmlResponseException => Loc.T("Error.HtmlResponse"),
        DebridException debrid => Debrid(debrid),
        StorageException or InvalidDataException => ex.Message,
        // Fehler des Dateisystems sind lokal; was ohne solchen Code abbricht, ist die Verbindung.
        IOException io => LocalFile(io) ?? (Classify(io) != IoErrorKind.NotLocal ? Loc.T("Error.Storage", io.Message) : Loc.T("Error.ConnectionAborted")),
        UnauthorizedAccessException => Loc.T("Error.NoAccess"),
        NotSupportedException => Loc.T("Error.Unsupported"),
        _ => ex.Message,
    };

    private static IoErrorKind Classify(IOException ex) => PlatformServices.IoErrors.Classify(ex);

    /// <summary>Text für einen lokalen Dateifehler, den ein Benutzer beheben kann (Ordner weg, Pfad zu lang, kein Zugriff), sonst null.</summary>
    private static string? LocalFile(Exception ex) => ex switch
    {
        PathTooLongException => Loc.T("Error.PathTooLong"),
        DirectoryNotFoundException or DriveNotFoundException => Loc.T("Error.FolderNotFound"),
        UnauthorizedAccessException => Loc.T("Error.NoAccess"),
        IOException io => Classify(io) switch
        {
            IoErrorKind.PathTooLong => Loc.T("Error.PathTooLong"),
            IoErrorKind.TargetGone => Loc.T("Error.FolderNotFound"),
            IoErrorKind.NoAccess => Loc.T("Error.NoAccess"),
            _ => null,
        },
        _ => null,
    };

    /// <summary>Ohne Key und ohne Link: nur die Art des Fehlers und bei unbekannten Fällen der Code des Dienstes.</summary>
    public static string Debrid(DebridException ex) => ex.Service == RapidgatorProvider.ProviderId
        ? Rapidgator(ex)
        : ex.Error == DebridError.Other
            ? Loc.T("Error.Debrid.Other", ex.Code ?? "?")
            : Loc.T("Error.Debrid." + ex.Error);

    /// <summary>Wie <see cref="Debrid"/>, mit der bereinigten Erklärung des Servers (die Doku empfiehlt, sie zu zeigen).</summary>
    private static string Rapidgator(DebridException ex)
    {
        string text = ex.Error switch
        {
            DebridError.Other => Loc.T("Error.Rapidgator.Other", ex.Code ?? "?"),
            DebridError.TrafficExhausted when ex.TrafficLeft >= 0 =>
                Loc.T("Error.Rapidgator.TrafficLeft", Format.Bytes(ex.TrafficLeft), Format.Bytes(ex.TrafficNeeded)),
            _ => Loc.T("Error.Rapidgator." + ex.Error),
        };
        return ex.Detail is null ? text : Loc.T("Error.Rapidgator.Detail", text, ex.Detail);
    }

    private static string Protocol(ServerProtocolException ex) => ex.Problem switch
    {
        ProtocolProblem.MissingContentRange => Loc.T("Error.Protocol.MissingContentRange"),
        ProtocolProblem.WrongRange => Loc.T("Error.Protocol.WrongRange"),
        ProtocolProblem.RangeIgnored => Loc.T("Error.Protocol.RangeIgnored"),
        _ => Loc.T("Error.Protocol.UnexpectedStatus", ex.Status),
    };

    private static string Status(HttpStatusCode code) => (int)code switch
    {
        400 or 401 or 403 or 404 or 410 or 429 or 500 or 502 or 503 or 504 => Loc.T("Error.Http." + (int)code),
        var n => Loc.T("Error.Http.Other", n),
    };

    private static string? Socket(HttpRequestException ex) => (ex.InnerException as SocketException)?.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData => Loc.T("Error.HostNotFound"),
        SocketError.ConnectionRefused => Loc.T("Error.ConnectionRefused"),
        SocketError.ConnectionReset => Loc.T("Error.ConnectionReset"),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => Loc.T("Error.NetworkUnreachable"),
        SocketError.TimedOut => Loc.T("Error.Timeout"),
        _ => null,
    };
}
