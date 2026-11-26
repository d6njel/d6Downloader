using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Debrid;

namespace D6Downloader.Hosters.Rapidgator;

/// <summary>Einstellmöglichkeiten für Tests (lokale Attrappe); die Vorgaben sind die echten.</summary>
public sealed class RapidgatorOptions
{
    public Uri? BaseUri { get; init; }

    /// <summary>Welche Hosts eine Download-Adresse haben darf.</summary>
    public Func<Uri, bool> AllowDownloadHost { get; init; } = RapidgatorProvider.IsRapidgatorHost;

    /// <summary>Eine <c>http://</c>-Download-Adresse zuerst über HTTPS versuchen.</summary>
    public bool PreferHttps { get; init; } = true;

    public TimeSpan RetryPause { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Rapidgator mit dem eigenen Premium-Konto. Das „apiKey“ der Schnittstelle ist hier das Token der
/// <see cref="RapidgatorSession"/>; lehnt der Server es ab, wird es einmal erneuert, die Versuche zählen dabei weiter.
/// </summary>
public sealed class RapidgatorProvider : IDebridProvider
{
    public const string ProviderId = "rapidgator";

    /// <summary>Verbindungen zu den Download-Servern, über alle Downloads. Die echten Grenzen sind nicht dokumentiert.</summary>
    public const int DefaultConnections = 4;

    private readonly RapidgatorApi _api;
    private readonly RapidgatorSession _session;
    private readonly RapidgatorOptions _options;

    public RapidgatorProvider(RapidgatorApi api, RapidgatorSession session, RapidgatorOptions? options = null)
    {
        _api = api;
        _session = session;
        _options = options ?? new RapidgatorOptions();
    }

    public string Name => "Rapidgator";
    public string Id => ProviderId;
    public IReadOnlyList<string>? FixedHosts => RapidgatorLinks.Domains;
    public bool Handles(Uri uri) => RapidgatorLinks.Parse(uri) is not null;
    public int CheckBatchSize => RapidgatorApi.CheckLinkLimit;

    /// <summary>Längste Wartezeit, die ein <c>delay</c> des Servers verlangen darf.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>So alt darf der Kontostand für die Traffic-Vorprüfung sein.</summary>
    public TimeSpan AccountMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    public static bool IsRapidgatorHost(Uri uri) =>
        uri.IdnHost.Equals("rapidgator.net", StringComparison.OrdinalIgnoreCase)
        || uri.IdnHost.EndsWith(".rapidgator.net", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(RapidgatorLinks.Domains);

    public async Task<DebridAccount> CheckAccountAsync(string apiKey, CancellationToken ct)
    {
        var token = new TokenRef(apiKey);
        var user = await WithTokenAsync(token, (t, a) => _api.UserInfoAsync(t, a, ct), ct).ConfigureAwait(false);
        return _session.SetAccount(user);
    }

    public async Task<DebridLink> UnlockAsync(string apiKey, string link, string? password, IProgress<DebridWait>? waiting,
        CancellationToken ct)
    {
        var parsed = RapidgatorLinks.Parse(link) ?? throw RapidgatorApi.Error(DebridError.HostNotSupported);
        if (parsed.IsFolder) throw RapidgatorApi.Error(DebridError.FolderNotSupported);

        var token = new TokenRef(apiKey);
        var file = await WithTokenAsync(token, (t, a) => _api.FileInfoAsync(t, parsed.FileId, a, ct), ct).ConfigureAwait(false);
        await CheckTrafficAsync(token, file.Size, ct).ConfigureAwait(false);

        (string url, long delay) answer;
        try
        {
            answer = await WithTokenAsync(token, (t, a) => _api.DownloadUrlAsync(t, parsed.FileId, a, ct), ct)
                .ConfigureAwait(false);
        }
        catch (DebridException ex) when (ex.Error == DebridError.TrafficExhausted)
        {
            _session.ForgetAccount();
            throw;
        }

        // Nur Rapidgator-Hosts: Die Adresse trägt eine Sitzungskennung und die Datei landet auf der Platte.
        if (!Uri.TryCreate(answer.url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || !_options.AllowDownloadHost(uri))
            throw RapidgatorApi.Error(DebridError.BadResponse);

        await WaitAsync(answer.delay, waiting, ct).ConfigureAwait(false);

        // Die Doku zeigt http://. HTTPS wird zuerst versucht, die Engine fällt auf die gelieferte Adresse zurück.
        string? secure = uri.Scheme == Uri.UriSchemeHttp && _options.PreferHttps
            ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.AbsoluteUri
            : null;
        return new DebridLink(uri.AbsoluteUri, file.Name ?? parsed.NameHint, file.Size, "rapidgator.net", secure);
    }

    /// <summary>Delay laut <c>file/download</c> (Sekunden, vermutlich nur Free-Konten), abbrechbar, mit Obergrenze.</summary>
    private async Task WaitAsync(long seconds, IProgress<DebridWait>? waiting, CancellationToken ct)
    {
        if (seconds <= 0) return;
        if (seconds > MaxDelay.TotalSeconds) throw RapidgatorApi.Error(DebridError.GenerationFailed);
        for (long left = seconds; left > 0; left--)
        {
            waiting?.Report(new DebridWait((int)left));
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Vorprüfung mit dem höchstens <see cref="AccountMaxAge"/> alten Kontostand. Lässt sich der nicht holen, entscheidet der
    /// Server beim Download selbst. Ohne Angabe zum Traffic wird nicht geprüft.
    /// </summary>
    private async Task CheckTrafficAsync(TokenRef token, long size, CancellationToken ct)
    {
        var account = _session.RecentAccount(AccountMaxAge);
        if (account is null)
        {
            try
            {
                account = await CheckAccountAsync(token.Value, ct).ConfigureAwait(false);
                token.Value = await _session.GetAsync(ct).ConfigureAwait(false);
            }
            catch (DebridException ex) when (ex.Error is DebridError.RateLimited or DebridError.ServiceUnavailable
                                                 or DebridError.BadResponse or DebridError.Other)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                return;
            }
        }
        if (!account.IsPremium) throw RapidgatorApi.Error(DebridError.NotPremium);
        if (account.TrafficLeft is { } left && size >= 0 && size > left)
            throw new DebridException(DebridError.TrafficExhausted)
            {
                Service = ProviderId, TrafficLeft = Math.Max(0, left), TrafficNeeded = size,
            };
    }

    public async Task<IReadOnlyList<LinkInfo>> CheckLinksAsync(string apiKey, IReadOnlyList<string> links, CancellationToken ct)
    {
        var result = links.Select(l => new LinkInfo(l, LinkState.Unknown, null, -1)).ToArray();
        var files = Enumerable.Range(0, links.Count).Where(i => RapidgatorLinks.Parse(links[i]) is { IsFolder: false }).ToList();
        var token = new TokenRef(apiKey);
        foreach (int[] batch in files.Chunk(RapidgatorApi.CheckLinkLimit))
        {
            var asked = batch.Select(i => links[i]).ToList();
            var answers = await WithTokenAsync(token, (t, a) => _api.CheckLinksAsync(t, asked, a, ct), ct).ConfigureAwait(false);
            for (int n = 0; n < batch.Length; n++)
            {
                string link = asked[n];
                string id = RapidgatorLinks.Parse(link)!.FileId;
                // Zuordnung über die zurückgemeldete Adresse, sonst über die Datei-ID, sonst bei gleicher Anzahl über den Platz.
                int at = answers.FindIndex(x => x.Url == link || x.Url == RapidgatorApi.Escape(link));
                if (at < 0) at = answers.FindIndex(x => RapidgatorLinks.Parse(x.Url)?.FileId == id);
                if (at < 0 && answers.Count == asked.Count && answers[n].Url.Length == 0) at = n;
                if (at < 0) continue;
                var answer = answers[at];
                result[batch[n]] = answer.Status?.ToUpperInvariant() switch
                {
                    "ACCESS" => new LinkInfo(link, LinkState.Online,
                        string.IsNullOrWhiteSpace(answer.FileName) ? null : answer.FileName, answer.Size < 0 ? -1 : answer.Size),
                    // Die Bedeutung ist nicht dokumentiert, „gelöscht“ wäre geraten.
                    "NO ACCESS" => new LinkInfo(link, LinkState.NoAccess, null, -1),
                    _ => result[batch[n]],
                };
            }
        }
        return result;
    }

    private sealed class TokenRef
    {
        public TokenRef(string value) => Value = value;
        public string Value;
    }

    /// <summary>
    /// Führt eine Anfrage aus. Wird das Token abgelehnt, wird es erneuert und die Anfrage wiederholt, innerhalb derselben
    /// höchstens <see cref="Attempts.Max"/> Versuche.
    /// </summary>
    private async Task<T> WithTokenAsync<T>(TokenRef token, Func<string, Attempts, Task<T>> call, CancellationToken ct)
    {
        var attempts = new Attempts();
        while (true)
        {
            try
            {
                var result = await call(token.Value, attempts).ConfigureAwait(false);
                _session.MarkAccepted(token.Value);
                return result;
            }
            catch (DebridException ex) when (ex.Error == DebridError.LinkDown)
            {
                _session.MarkAccepted(token.Value);
                throw;
            }
            catch (DebridException ex) when (ex.Error == DebridError.SessionExpired && attempts.Left > 0)
            {
                if (!await _session.RefreshAfterRejectAsync(token.Value, ct).ConfigureAwait(false)) throw;
                token.Value = await _session.GetAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
