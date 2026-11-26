using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Tests;

/// <summary>
/// Attrappe der Rapidgator-API v2 auf 127.0.0.1, Antworten nach <c>docs/rapidgator-api-v2.md</c>. Was dort nicht steht
/// (abgelaufenes Token, fehlender Code, Traffic, Captcha), ist hier angenommen. Alle Zugangsdaten sind ausgedacht.
/// </summary>
public sealed class FakeRapidgator : IDisposable
{
    public const string Login = "tester@example.invalid";
    public const string Password = "pw-Geheim&=1 x";
    public const string Code = "493817";
    public const string SessionSecret = "SESSIONSECRET0123456789";

    public const string FileId = "0123456789abcdef0123456789abcdef";
    public const string FileUrl = "https://rapidgator.net/file/" + FileId + "/Pack.part1.rar.html";

    public sealed record Request(string Method, string PathAndQuery, string Endpoint, Dictionary<string, string> Form, string Body);

    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, bool> _tokens = new();
    private int _tokenCounter;

    public Uri BaseUri { get; }
    public ConcurrentQueue<Request> Log { get; } = new();

    public int Count(string endpoint) => Log.Count(r => r.Endpoint == endpoint);
    public int Logins => Count("user/login");
    public string[] IssuedTokens => _tokens.Keys.ToArray();

    public bool TwoFactor { get; set; }
    public bool Premium { get; set; } = true;
    /// <summary>Rohes JSON für <c>traffic.left</c>, null = <c>traffic</c> ist null.</summary>
    public string? TrafficLeft { get; set; } = "1000000000";
    public long FileSize { get; set; } = 12345;
    public string DownloadUrl { get; set; } = "http://127.0.0.1:1/?r=download/index&session_id=" + SessionSecret;
    /// <summary>Rohes JSON für <c>delay</c>.</summary>
    public string Delay { get; set; } = "0";

    /// <summary>Wartezeit vor der Antwort auf eine Anmeldung.</summary>
    public TimeSpan LoginDelay { get; set; }

    /// <summary>Wie ein abgelehntes Token beantwortet wird (Annahme).</summary>
    public (int Status, string Details) Rejection { get; set; } = (401, "Error: Wrong token");

    /// <summary>Ersetzt die Antwort eines Endpunkts (HTTP-Status, Körper); null = normal.</summary>
    public Func<Request, (int Status, string Body)?>? Override { get; set; }

    /// <summary>Alle Anfragen werden dorthin weitergeleitet (307).</summary>
    public string? RedirectTo { get; set; }

    /// <summary>Alle bisher ausgegebenen Token gelten nicht mehr.</summary>
    public void ExpireTokens()
    {
        foreach (string token in _tokens.Keys) _tokens[token] = false;
    }

    /// <summary>Jedes Token, auch ein neues, wird abgelehnt.</summary>
    public bool RejectAllTokens { get; set; }

    public FakeRapidgator()
    {
        int port = 0;
        for (int attempt = 1; ; attempt++)
        {
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 10) { }
        }
        BaseUri = new Uri($"http://127.0.0.1:{port}/api/v2/");
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            string body = "";
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) form[WebUtility.UrlDecode(pair[..eq])] = WebUtility.UrlDecode(pair[(eq + 1)..]);
            }
            string path = ctx.Request.Url!.AbsolutePath;
            string endpoint = path.StartsWith("/api/v2/", StringComparison.Ordinal) ? path["/api/v2/".Length..] : path;
            var request = new Request(ctx.Request.HttpMethod, ctx.Request.Url.PathAndQuery, endpoint, form, body);
            Log.Enqueue(request);

            if (RedirectTo is not null)
            {
                ctx.Response.StatusCode = 307;
                ctx.Response.RedirectLocation = RedirectTo;
                ctx.Response.Close();
                return;
            }
            if (endpoint == "user/login" && LoginDelay > TimeSpan.Zero) await Task.Delay(LoginDelay).ConfigureAwait(false);

            var (status, json) = Override?.Invoke(request) ?? Answer(request);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch
        {
            // Verbindung vom Test geschlossen
        }
    }

    public static (int, string) Ok(string response) => (200, "{\"response\":" + response + ",\"status\":200,\"details\":null}");

    public static (int, string) Fail(int status, string details) =>
        (status, "{\"response\":null,\"status\":" + status + ",\"details\":" + JsonSerializer.Serialize(details) + "}");

    private string User() => "{\"email\":\"" + Login + "\",\"is_premium\":" + (Premium ? "true" : "false")
        + ",\"premium_end_time\":" + (Premium ? "\"1893456000\"" : "null")
        + ",\"state\":1,\"state_label\":\"Normal\",\"traffic\":"
        + (TrafficLeft is null ? "null" : "{\"total\":\"5000000000\",\"left\":" + TrafficLeft + "}")
        + ",\"storage\":{\"total\":\"100\",\"left\":50}}";

    private (int, string) Answer(Request r)
    {
        string Value(string key) => r.Form.GetValueOrDefault(key, "");
        if (r.Endpoint == "user/login")
        {
            if (Value("login") != Login || Value("password") != Password) return Fail(401, "Error: Login or password is wrong");
            if (TwoFactor && Value("code") != Code) return Fail(401, "Error: Invalid auth code");
            string token = "tok" + Interlocked.Increment(ref _tokenCounter) + "secretTOKENvalue";
            _tokens[token] = true;
            return Ok("{\"token\":\"" + token + "\",\"user\":" + User() + "}");
        }
        if (RejectAllTokens || !_tokens.TryGetValue(Value("token"), out bool valid) || !valid)
            return Fail(Rejection.Status, Rejection.Details);
        switch (r.Endpoint)
        {
            case "user/info":
                return Ok("{\"user\":" + User() + "}");
            case "file/info":
                return Value("file_id") != FileId ? Fail(404, "Error: File not found")
                    : Ok("{\"file\":{\"file_id\":\"" + FileId + "\",\"mode\":1,\"mode_label\":\"Premium only\",\"name\":\"Pack.part1.rar\","
                         + "\"hash\":\"" + FileId + "\",\"size\":\"" + FileSize + "\",\"url\":\"http://rapidgator.net/file/" + FileId + "/Pack.part1.rar\"}}");
            case "file/download":
                return Value("file_id") != FileId ? Fail(404, "Error: File not found")
                    : Ok("{\"download_url\":" + JsonSerializer.Serialize(DownloadUrl) + ",\"delay\":" + Delay + "}");
            case "file/check_link":
                var links = Value("url").Split(',');
                if (links.Length > 25) return Fail(400, "Error: Many links");
                return Ok("[" + string.Join(",", links.Select(l => l.Contains("noaccess")
                    ? "{\"url\":" + JsonSerializer.Serialize(l) + ",\"status\":\"NO ACCESS\"}"
                    : "{\"url\":" + JsonSerializer.Serialize(l) + ",\"status\":\"ACCESS\",\"filename\":\"Checked.bin\",\"size\":\"77\"}")) + "]");
            default:
                return Fail(404, "Error: Not found");
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); }
        catch { }
    }
}
