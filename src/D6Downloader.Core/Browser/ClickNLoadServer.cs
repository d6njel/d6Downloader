using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Downloads;

namespace D6Downloader.Browser;

/// <summary>
/// Click'n'Load-Server: nimmt Links von Webseiten an (<c>/flash/add</c>, <c>/flash/addcrypted2</c> und <c>/flash/addcnl</c>,
/// per POST oder GET) und gibt den Text an <see cref="LinksReceived"/>. Er bindet nur an die Loopback-Adressen 127.0.0.1 und,
/// wenn möglich, ::1 (nie an alle Adressen), prüft den Host-Header gegen DNS-Rebinding,
/// begrenzt Kopf, Körper, Wartezeit, gleichzeitige Verbindungen und Anfragen pro Minute und startet nie einen Download:
/// Was ankommt, landet im Hinzufügen-Dialog. Ein eigener kleiner HTTP-Leser statt <c>HttpListener</c>, damit die Bindung an die
/// Loopback-Adresse und alle Grenzen nicht vom Treiber abhängen.
/// </summary>
public sealed class ClickNLoadServer : IDisposable
{
    public const int DefaultPort = 9666;
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxConnections = 8;
    public const int MaxSubmissionsPerMinute = 30;

    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly TcpListener? _listenerV6;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);
    private readonly Queue<DateTime> _recent = new();
    private readonly object _recentLock = new();

    private ClickNLoadServer(TcpListener listener, TcpListener? listenerV6, int port)
    {
        _listener = listener;
        _listenerV6 = listenerV6;
        Port = port;
    }

    /// <summary>Der gebundene Port (bei Start mit Port 0 der vom System gewählte).</summary>
    public int Port { get; }

    /// <summary>Links (und Archivpasswörter) einer gültigen Anfrage, aufgerufen auf einem Thread-Pool-Thread.</summary>
    public event Action<ClickNLoadSubmission>? LinksReceived;

    /// <summary>
    /// Startet den Server auf 127.0.0.1 und zusätzlich, wenn möglich, auf ::1 mit demselben Port (<c>localhost</c> löst bei
    /// manchen Browsern zuerst dorthin auf). Wirft <see cref="SocketException"/> (<c>AddressAlreadyInUse</c>), wenn der Port auf
    /// 127.0.0.1 belegt ist, etwa durch JDownloader. Fehler bei ::1 werden übergangen.
    /// </summary>
    public static ClickNLoadServer Start(int port = DefaultPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start(16);
        int bound = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new ClickNLoadServer(listener, TryStartV6(bound), bound);
        _ = Task.Run(() => server.AcceptLoop(server._listener));
        if (server._listenerV6 is { } v6) _ = Task.Run(() => server.AcceptLoop(v6));
        return server;
    }

    private static TcpListener? TryStartV6(int port)
    {
        if (!Socket.OSSupportsIPv6) return null;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, port) { ExclusiveAddressUse = true };
            listener.Server.DualMode = false; // IPv6Only
            listener.Start(16);
            return listener;
        }
        catch (SocketException)
        {
            listener?.Stop();
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Stop(_listener);
        if (_listenerV6 is not null) Stop(_listenerV6);

        static void Stop(TcpListener listener)
        {
            try { listener.Stop(); }
            catch (SocketException) { }
        }
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (!_slots.Wait(0))
            {
                client.Dispose(); // zu viele offene Verbindungen
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(client).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // eine kaputte Verbindung betrifft nur sich selbst
                }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    // ---------------------------------------------------------------- Anfrage lesen

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private async Task HandleAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(ConnectionTimeout);
        var stream = client.GetStream();
        try
        {
            var (request, error) = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            if (request is null)
            {
                if (error is not null) await RespondAsync(stream, error.Value.Status, error.Value.Text, PlainText, null, timeout.Token).ConfigureAwait(false);
                return;
            }
            var (status, text, contentType) = Route(request, out string? preflightHeaders);
            await RespondAsync(stream, status, text, contentType, preflightHeaders, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // zu langsam oder beendet
        }
    }

    private async Task<(Request?, (int Status, string Text)?)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length == buffer.Length) return (null, (431, "failed"));
            int n = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (n == 0) return (null, null);
            length += n;
            headerEnd = IndexOfHeaderEnd(buffer, length);
        }

        string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return (null, (400, "failed"));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return (null, (400, "failed"));
            headers.TryAdd(lines[i][..colon].Trim(), lines[i][(colon + 1)..].Trim());
        }
        if (!HostAllowed(headers.GetValueOrDefault("Host"))) return (null, (421, "failed"));
        if (headers.ContainsKey("Transfer-Encoding")) return (null, (411, "failed"));

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? cl) && (!int.TryParse(cl, out contentLength) || contentLength < 0))
            return (null, (400, "failed"));
        if (contentLength > MaxBodyBytes) return (null, (413, "failed"));

        var body = new byte[contentLength];
        int have = Math.Min(contentLength, length - (headerEnd + 4));
        Array.Copy(buffer, headerEnd + 4, body, 0, have);
        while (have < contentLength)
        {
            int n = await stream.ReadAsync(body.AsMemory(have), ct).ConfigureAwait(false);
            if (n == 0) return (null, (400, "failed"));
            have += n;
        }
        return (new Request(first[0], first[1], headers, body), null);
    }

    private static int IndexOfHeaderEnd(byte[] b, int length)
    {
        for (int i = 3; i < length; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }

    /// <summary>
    /// Nur die Loopback-Namen mit dem eigenen Port. Eine fremde Seite kann einen eigenen Namen auf 127.0.0.1 zeigen lassen
    /// (DNS-Rebinding), dann steht ihr Name im Host-Header und die Anfrage wird abgewiesen.
    /// </summary>
    private bool HostAllowed(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string name = host;
        string? port = null;
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            if (close < 0) return false;
            name = host[..(close + 1)];
            if (close + 1 < host.Length) port = host[(close + 2)..];
        }
        else if (host.LastIndexOf(':') is var colon and >= 0)
        {
            name = host[..colon];
            port = host[(colon + 1)..];
        }
        if (port is not null && port != Port.ToString()) return false;
        return name.Equals("127.0.0.1", StringComparison.Ordinal) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[::1]", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Routen

    private const string PlainText = "text/plain; charset=utf-8";

    // Damit prüfen Webseiten, ob ein Click'n'Load-Programm läuft: Sie binden das Skript ein und sehen nach der Variablen.
    private const string JdCheckScript = "jdownloader=true;\r\nvar version='d6downloader';\r\n";

    private const string CrossDomainPolicy =
        "<?xml version=\"1.0\"?>\r\n" +
        "<!DOCTYPE cross-domain-policy SYSTEM \"http://www.adobe.com/xml/dtds/cross-domain-policy.dtd\">\r\n" +
        "<cross-domain-policy>\r\n" +
        "<allow-access-from domain=\"*\" />\r\n" +
        "</cross-domain-policy>\r\n";

    private (int Status, string Text, string ContentType) Route(Request request, out string? preflight)
    {
        preflight = null;
        string path = request.Path;
        string queryText = "";
        int query = path.IndexOf('?');
        if (query >= 0)
        {
            queryText = path[(query + 1)..];
            path = path[..query];
        }
        path = path.TrimEnd('/');

        static (int, string, string) Plain(int status, string text) => (status, text, PlainText);

        if (request.Method == "OPTIONS")
        {
            preflight = request.Headers.GetValueOrDefault("Access-Control-Request-Headers");
            return Plain(204, "");
        }
        if (request.Method == "GET")
        {
            if (path is "/flash" or "")
                return Plain(200, "JDownloader"); // Webseiten erkennen den Dienst an dieser Antwort (so antworten auch andere Click'n'Load-Programme)
            if (path == "/jdcheck.js") return (200, JdCheckScript, "text/javascript; charset=utf-8");
            if (path == "/crossdomain.xml") return (200, CrossDomainPolicy, "application/xml");
        }

        if (request.Method is not ("GET" or "POST")) return Plain(404, "failed");
        if (path is not ("/flash/add" or "/flash/addcrypted2" or "/flash/addcnl")) return Plain(404, "failed");

        var form = FormBody.Parse(queryText);
        if (request.Method == "POST")
        {
            string type = request.Headers.GetValueOrDefault("Content-Type") ?? "";
            Dictionary<string, string>? body;
            if (type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                body = FormBody.Parse(Encoding.UTF8.GetString(request.Body));
            else if (type.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                body = FormBody.ParseMultipart(request.Body, type);
            else
                return Plain(415, "failed");
            if (body is null) return Plain(400, "failed");
            foreach (var (name, value) in body) form[name] = value; // der Körper hat Vorrang vor der Adresse
        }

        string text;
        if (path == "/flash/add")
        {
            text = form.GetValueOrDefault("urls") ?? form.GetValueOrDefault("url") ?? "";
        }
        else
        {
            if (!ClickNLoadCrypto.TryReadKey(form.GetValueOrDefault("jk"), out byte[] key)
                || !ClickNLoadCrypto.TryDecrypt(form.GetValueOrDefault("crypted"), key, out text))
                return Plain(400, "failed");
        }
        if (!LinkParser.ContainsLink(text)) return Plain(400, "failed");
        if (!AllowSubmission()) return Plain(429, "failed");

        LinksReceived?.Invoke(new ClickNLoadSubmission(text, ClickNLoadSubmission.ParsePasswords(form.GetValueOrDefault("passwords"))));
        return Plain(200, "success\r\n");
    }

    /// <summary>Höchstens <see cref="MaxSubmissionsPerMinute"/> angenommene Anfragen pro Minute.</summary>
    private bool AllowSubmission()
    {
        lock (_recentLock)
        {
            var now = DateTime.UtcNow;
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1)) _recent.Dequeue();
            if (_recent.Count >= MaxSubmissionsPerMinute) return false;
            _recent.Enqueue(now);
            return true;
        }
    }

    // ---------------------------------------------------------------- Antwort

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        404 => "Not Found",
        411 => "Length Required",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        421 => "Misdirected Request",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        _ => "Error",
    };

    /// <summary>
    /// Antwort mit den CORS-Headern, die Webseiten brauchen, um den Dienst anzusprechen (jede Herkunft, Vorabfrage mit OPTIONS,
    /// Zugriff aus dem öffentlichen ins lokale Netz für Chrome). Die Verbindung wird danach geschlossen.
    /// </summary>
    private static async Task RespondAsync(NetworkStream stream, int status, string text, string contentType, string? requestedHeaders,
        CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        // Nur harmlose Namen zurückgeben, keinen beliebigen Text aus der Anfrage in den Kopf übernehmen.
        string allowed = requestedHeaders is not null && IsHeaderList(requestedHeaders) ? requestedHeaders : "Content-Type";
        head.Append("Access-Control-Allow-Headers: ").Append(allowed).Append("\r\n");
        head.Append("Access-Control-Allow-Private-Network: true\r\n");
        head.Append("Access-Control-Max-Age: 600\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static bool IsHeaderList(string value)
    {
        if (value.Length > 200) return false;
        foreach (char c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or ',' or ' ')) return false;
        return true;
    }
}
