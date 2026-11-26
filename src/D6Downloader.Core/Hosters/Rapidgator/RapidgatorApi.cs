using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Debrid;

namespace D6Downloader.Hosters.Rapidgator;

/// <summary>Das Konto laut <c>user/login</c> und <c>user/info</c>. Traffic ist bei Free-Konten null.</summary>
public sealed record RapidgatorUser(string? Email, bool IsPremium, DateTimeOffset? PremiumUntil, long? TrafficLeft, long? TrafficTotal);

public sealed record RapidgatorFile(string? Name, long Size);

/// <summary>Wie viele Versuche eine Anfrage noch hat. Die Doku verlangt: nach spätestens drei aufgeben.</summary>
internal sealed class Attempts
{
    public const int Max = 3;
    public int Left = Max;
}

/// <summary>
/// Dünner Client der Rapidgator-API v2 (siehe <c>docs/rapidgator-api-v2.md</c>). Nur HTTPS (unverschlüsselt allein zu
/// 127.0.0.1, für Tests), nur POST mit Formulardaten: Passwort, Code und Token stehen nie in der Adresse. Der Client darf keine
/// Weiterleitungen folgen, sonst gingen die Formulardaten an ein anderes Ziel. Ausnahmen enthalten weder Zugangsdaten noch
/// Download-Adressen; die „details“ des Servers nur bereinigt.
/// </summary>
public sealed class RapidgatorApi
{
    public const string DefaultBase = "https://rapidgator.net/api/v2/";
    public const int CheckLinkLimit = 25;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _base;

    /// <param name="http">Eigener Client ohne automatische Weiterleitungen (siehe <see cref="CreateClient"/>).</param>
    /// <param name="baseUri">Nur für Tests. Muss auf <c>/</c> enden.</param>
    public RapidgatorApi(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _base = baseUri ?? new Uri(DefaultBase);
        if (_base.Scheme != Uri.UriSchemeHttps && !(_base.Scheme == Uri.UriSchemeHttp && _base.IsLoopback))
            throw new ArgumentException("Rapidgator nur über HTTPS.", nameof(baseUri));
    }

    /// <summary>
    /// Client für die API: keine Weiterleitungen, keine Cookies, Zertifikate werden immer geprüft (der Schalter „SSL-Fehler
    /// ignorieren“ der Downloads gilt hier nicht).
    /// </summary>
    public static HttpClient CreateClient(string? proxy, TimeSpan connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy.Trim());
            handler.UseProxy = true;
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
    }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause vor dem zweiten Versuch, vor dem dritten doppelt so lang.</summary>
    public TimeSpan RetryPause { get; set; } = TimeSpan.FromSeconds(2);

    // ---------------------------------------------------------------- Aufrufe

    /// <summary>Genau eine Anfrage, nie wiederholt: Zu viele falsche Anmeldungen führen zur IP-Sperre.</summary>
    internal async Task<(string Token, RapidgatorUser User)> LoginAsync(string login, string password, string? code,
        CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("login", login), new("password", password) };
        if (code is not null) form.Add(new("code", code));
        var response = await CallAsync("user/login", form, new Attempts { Left = 1 }, new[] { login, password, code }, true, code is not null, ct)
            .ConfigureAwait(false);
        string token = Str(response, "token") is { Length: > 0 } t ? t : throw Bad();
        var user = response.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
            ? ReadUser(u)
            // ohne Nutzerangaben gilt das Konto bis zur Abfrage von user/info nicht als bekannt
            : null;
        return (token, user ?? new RapidgatorUser(null, false, null, null, null));
    }

    internal async Task<RapidgatorUser> UserInfoAsync(string token, Attempts attempts, CancellationToken ct)
    {
        var response = await TokenCallAsync("user/info", token, null, attempts, ct).ConfigureAwait(false);
        return ReadUser(Object(response, "user"));
    }

    internal async Task<RapidgatorFile> FileInfoAsync(string token, string fileId, Attempts attempts, CancellationToken ct)
    {
        var response = await TokenCallAsync("file/info", token, new("file_id", fileId), attempts, ct).ConfigureAwait(false);
        var file = Object(response, "file");
        string? name = Str(file, "name");
        long size = Number(file, "size") ?? -1;
        return new RapidgatorFile(string.IsNullOrWhiteSpace(name) ? null : name, size < 0 ? -1 : size);
    }

    /// <returns>Die Download-Adresse (ungeprüft) und die Wartezeit in Sekunden.</returns>
    internal async Task<(string Url, long Delay)> DownloadUrlAsync(string token, string fileId, Attempts attempts,
        CancellationToken ct)
    {
        var response = await TokenCallAsync("file/download", token, new("file_id", fileId), attempts, ct).ConfigureAwait(false);
        string url = Str(response, "download_url") is { Length: > 0 } u ? u : throw Bad();
        return (url, Math.Max(0, Number(response, "delay") ?? 0));
    }

    /// <summary>Höchstens <see cref="CheckLinkLimit"/> Links. Liefert je Antworteintrag Adresse, Status, Name und Größe.</summary>
    internal async Task<List<(string Url, string? Status, string? FileName, long Size)>> CheckLinksAsync(string token,
        IReadOnlyList<string> links, Attempts attempts, CancellationToken ct)
    {
        if (links.Count > CheckLinkLimit) throw new ArgumentException("Zu viele Links.", nameof(links));
        var joined = new StringBuilder();
        foreach (string link in links)
        {
            if (joined.Length > 0) joined.Append(',');
            joined.Append(Escape(link));
        }
        var response = await TokenCallAsync("file/check_link", token, new("url", joined.ToString()), attempts, ct)
            .ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Array) throw Bad();
        var result = new List<(string, string?, string?, long)>();
        foreach (var item in response.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { result.Add(("", null, null, -1)); continue; }
            result.Add((Str(item, "url") ?? "", Str(item, "status"), Str(item, "filename"), Number(item, "size") ?? -1));
        }
        return result;
    }

    /// <summary>Ein Komma trennt die Links: Ein Komma im Dateinamen muss kodiert werden.</summary>
    internal static string Escape(string link) => link.Replace(",", "%2C");

    private Task<JsonElement> TokenCallAsync(string path, string token, KeyValuePair<string, string>? extra, Attempts attempts,
        CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("token", token) };
        if (extra is { } pair) form.Add(pair);
        return CallAsync(path, form, attempts, new[] { token }, false, false, ct);
    }

    private async Task<JsonElement> CallAsync(string path, List<KeyValuePair<string, string>> form, Attempts attempts,
        string?[] secrets, bool login, bool codeSent, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            attempts.Left--;
            TimeSpan? retryAfter = null;
            Exception failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path));
                request.Headers.UserAgent.ParseAdd("d6Downloader/0.1");
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new FormUrlEncodedContent(form);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                int status;
                string body;
                try
                {
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                        .ConfigureAwait(false);
                    status = (int)response.StatusCode;
                    retryAfter = response.Headers.RetryAfter?.Delta;
                    body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Rapidgator antwortet nicht.");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Neue Ausnahme ohne den ursprünglichen Text; die innere (Socket, TLS) braucht die Anzeige.
                    throw new HttpRequestException("Rapidgator ist nicht erreichbar.", ex.InnerException);
                }
                return Parse(status, body, secrets, login, codeSent);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                           || ex is DebridException { Error: DebridError.RateLimited or DebridError.ServiceUnavailable })
            {
                failure = ex;
            }
            if (attempts.Left <= 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            var pause = RetryPause * attempt;
            if (retryAfter > pause) pause = retryAfter.Value < MaxRetryAfter ? retryAfter.Value : MaxRetryAfter;
            await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- Antwort

    private static JsonElement Parse(int httpStatus, string body, string?[] secrets, bool login, bool codeSent)
    {
        JsonElement root = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            if (httpStatus == 429) throw Error(DebridError.RateLimited, httpStatus);
            if (httpStatus >= 500) throw Error(DebridError.ServiceUnavailable, httpStatus);
            throw Bad();
        }

        long status = Number(root, "status") ?? httpStatus;
        bool ok = status == 200 && httpStatus is >= 200 and < 300;
        if (ok)
        {
            if (root.TryGetProperty("response", out var response) && response.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return response;
            throw Bad();
        }
        if (status == 200) status = httpStatus;
        string? details = Str(root, "details");
        throw Error(Classify((int)status, details, login, codeSent), status, Clean(details, secrets));
    }

    /// <summary>
    /// Ordnet einen Fehler ein. Dokumentiert sind nur „Login or password is wrong“, „Invalid auth code“, „User not found“,
    /// „File not found“ und „Many links“. Alles andere (abgelaufenes Token, fehlender 2FA-Code, Traffic, Captcha, Sperre) ist
    /// eine Annahme über Status und Stichwörter; Unbekanntes bleibt <see cref="DebridError.Other"/>.
    /// </summary>
    internal static DebridError Classify(int status, string? details, bool login, bool codeSent)
    {
        string d = details?.ToLowerInvariant() ?? "";
        bool Has(params string[] words) => Array.Exists(words, w => d.Contains(w, StringComparison.Ordinal));

        if (Has("captcha")) return DebridError.LoginBlocked;
        if (!login && Has("traffic", "bandwidth", "quota")) return DebridError.TrafficExhausted;
        if (status == 429 || Has("too many requests")) return DebridError.RateLimited;
        if (status >= 500) return DebridError.ServiceUnavailable;
        if (login)
        {
            if (Has("auth code", "2fa", "two-factor", "two factor", "verification code"))
                return codeSent ? DebridError.TwoFactorWrong : DebridError.TwoFactorRequired;
            if (Has("password is wrong", "wrong password", "login or password")) return DebridError.BadCredentials;
            if (Has("block", "banned", "suspend", "locked", "confirm", "verify", "too many")) return DebridError.LoginBlocked;
            return status switch { 401 => DebridError.BadCredentials, 403 => DebridError.LoginBlocked, _ => DebridError.Other };
        }
        // „File not found“ und „User not found“ sind beide 404, bedeuten aber Verschiedenes.
        if (Has("file not found")) return DebridError.LinkDown;
        if (Has("user not found")) return DebridError.SessionExpired;
        if (Has("premium")) return DebridError.NotPremium;
        if (status == 401 || Has("token", "session", "not logged", "unauthorized")) return DebridError.SessionExpired;
        return DebridError.Other;
    }

    private static readonly Regex Scrub = new(
        @"\S*://\S*|\S*(session_id|token|password|code)\s*=\s*\S*|\S+@\S+|[A-Za-z0-9_\-+/=%]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Die „details“ des Servers für die Anzeige: ohne Adressen, lange Zeichenfolgen und Steuerzeichen, gekürzt. Nennt der Text
    /// eine gesendete Zugangsangabe, entfällt er ganz.
    /// </summary>
    internal static string? Clean(string? details, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(details)) return null;
        foreach (string? secret in secrets)
        {
            if (string.IsNullOrEmpty(secret)) continue;
            if (details.Contains(secret, StringComparison.OrdinalIgnoreCase)
                || details.Contains(Uri.EscapeDataString(secret), StringComparison.OrdinalIgnoreCase)
                || details.Contains(WebUtility.UrlEncode(secret), StringComparison.OrdinalIgnoreCase))
                return null;
        }
        var text = new StringBuilder(details.Length);
        foreach (char c in details) text.Append(char.IsControl(c) ? ' ' : c);
        string clean = Scrub.Replace(text.ToString(), "…").Trim();
        if (clean.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) clean = clean[6..].Trim();
        if (clean.Length > 150) clean = clean[..150] + "…";
        return clean.Length == 0 ? null : clean;
    }

    internal static DebridException Error(DebridError error, long? status = null, string? detail = null) =>
        new(error, status?.ToString(CultureInfo.InvariantCulture)) { Service = RapidgatorProvider.ProviderId, Detail = detail };

    private static DebridException Bad() => Error(DebridError.BadResponse);

    private static JsonElement Object(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw Bad();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Zahl, die auch als Text kommen kann (die Doku zeigt das bei <c>storage.total</c>). Null, wenn keine da ist.</summary>
    private static long? Number(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt64(out long n) ? n : v.TryGetDouble(out double d) && d is > -9e18 and < 9e18 ? (long)d : null;
        return v.ValueKind == JsonValueKind.String
               && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    private static RapidgatorUser ReadUser(JsonElement user)
    {
        if (!user.TryGetProperty("is_premium", out var p)) throw Bad();
        bool premium = p.ValueKind == JsonValueKind.True
            || (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int i) && i == 1)
            || (p.ValueKind == JsonValueKind.String && p.GetString() is "1" or "true");
        long? end = Number(user, "premium_end_time"); // vermutlich Sekunden seit 1970
        DateTimeOffset? until = end is > 0 && end <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(end.Value)
            : null;
        long? left = null, total = null;
        if (user.TryGetProperty("traffic", out var traffic) && traffic.ValueKind == JsonValueKind.Object)
        {
            left = Number(traffic, "left");
            total = Number(traffic, "total");
        }
        return new RapidgatorUser(Str(user, "email"), premium, premium ? until : null, left, total);
    }
}
