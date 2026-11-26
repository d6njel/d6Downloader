using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Debrid;
using D6Downloader.Downloads;
using D6Downloader.Engine;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Platform;
using D6Downloader.Sheets;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Rapidgator gegen <see cref="FakeRapidgator"/> (127.0.0.1, ausgedachte Zugangsdaten, nie der echte Dienst).</summary>
public class RapidgatorTests : IDisposable
{
    private const string Login = FakeRapidgator.Login, Password = FakeRapidgator.Password, Code = FakeRapidgator.Code;
    private const string FileUrl = FakeRapidgator.FileUrl;

    private readonly FakeRapidgator _fake = new();
    private readonly List<HttpClient> _clients = new();

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _fake.Dispose();
    }

    private RapidgatorOptions Options(bool preferHttps = false) => new()
    {
        BaseUri = _fake.BaseUri,
        AllowDownloadHost = u => u.Host == "127.0.0.1",
        PreferHttps = preferHttps,
        RetryPause = TimeSpan.FromMilliseconds(10),
    };

    private sealed record Rig(RapidgatorSettings Settings, RapidgatorSession Session, RapidgatorProvider Provider, DebridService Service)
    {
        public Task<DebridAccount> LogIn(string password = Password, string? code = null) =>
            Session.LoginAsync(Login, password, code, default);

        public Task<DebridLink?> Unlock(string url = FileUrl, CancellationToken ct = default) => Service.TryUnlockAsync(url, null, ct);
    }

    private Rig Create(RapidgatorSettings? settings = null, RapidgatorOptions? options = null, Func<string, string>? protect = null)
    {
        settings ??= new RapidgatorSettings();
        options ??= Options();
        var client = RapidgatorApi.CreateClient(null, TimeSpan.FromSeconds(5));
        _clients.Add(client);
        var api = new RapidgatorApi(client, options.BaseUri) { RetryPause = options.RetryPause };
        var session = new RapidgatorSession(api, () => settings, protect);
        var provider = new RapidgatorProvider(api, session, options);
        var service = new DebridService(provider, session, () => settings is { Enabled: true, HasCredential: true });
        return new Rig(settings, session, provider, service);
    }

    private static async Task<DebridException> Fails(DebridError expected, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<DebridException>(action);
        Assert.Equal(expected, ex.Error);
        return ex;
    }

    // ---------------------------------------------------------------- Links

    [Theory]
    [InlineData("https://rapidgator.net/file/0123456789abcdef0123456789abcdef", null)]
    [InlineData("http://www.rapidgator.net/file/0123456789ABCDEF0123456789ABCDEF/a%20b.rar.html", "a b.rar")]
    [InlineData("HTTPS://RapidGator.net/File/0123456789abcdef0123456789abcdef/x.zip", "x.zip")]
    [InlineData("https://rg.to/file/12345678/x.zip.html", "x.zip")]
    [InlineData("https://www.rg.to/file/0123456789abcdef0123456789abcdef/", null)]
    public void File_links_are_recognized(string url, string? name)
    {
        var link = RapidgatorLinks.Parse(url);
        Assert.NotNull(link);
        Assert.False(link!.IsFolder);
        Assert.Equal(name, link.NameHint);
    }

    [Theory]
    [InlineData("https://notrapidgator.net/file/0123456789abcdef0123456789abcdef")]
    [InlineData("https://rapidgator.net.example.com/file/0123456789abcdef0123456789abcdef")]
    [InlineData("https://example.com/rapidgator.net/file/0123456789abcdef0123456789abcdef")]
    [InlineData("ftp://rapidgator.net/file/0123456789abcdef0123456789abcdef")]
    [InlineData("https://rapidgator.net/article/api/index")]
    [InlineData("https://rapidgator.net/file/abc")]
    [InlineData("https://rapidgator.net/file")]
    [InlineData("not a link")]
    public void Other_links_are_not_recognized(string url) => Assert.Null(RapidgatorLinks.Parse(url));

    [Fact]
    public async Task Folder_links_are_refused_with_a_clear_message_without_a_request()
    {
        const string folder = "https://rapidgator.net/folder/123/name.html";
        Assert.True(RapidgatorLinks.Parse(folder)!.IsFolder);
        var rig = Create();
        await rig.LogIn();
        int before = _fake.Log.Count;

        var ex = await Fails(DebridError.FolderNotSupported, () => rig.Unlock(folder));

        Assert.Equal(before, _fake.Log.Count);
        Assert.Equal(Loc.T("Error.Rapidgator.FolderNotSupported"), ErrorText.Describe(ex));
    }

    // ---------------------------------------------------------------- Anmeldung

    [Fact]
    public async Task Login_posts_form_data_and_stores_only_encrypted_values()
    {
        var rig = Create();
        var account = await rig.LogIn();

        var request = Assert.Single(_fake.Log);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v2/user/login", request.PathAndQuery);
        Assert.Equal(Password, request.Form["password"]);
        Assert.False(request.Form.ContainsKey("code"));
        Assert.True(account.IsPremium);
        Assert.Equal(1_000_000_000, account.TrafficLeft);
        Assert.Equal(5_000_000_000, account.TrafficTotal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), account.PremiumUntil);
        Assert.Equal(RapidgatorState.LoggedIn, rig.Session.State);
        Assert.True(rig.Settings.Enabled);

        var app = new AppSettings { Rapidgator = rig.Settings };
        string json = Encoding.UTF8.GetString(app.ToJson());
        Assert.DoesNotContain("Geheim", json);
        Assert.DoesNotContain("secretTOKEN", json);
        Assert.Contains("v1:", rig.Settings.ProtectedPassword);
        Assert.Contains("v1:", rig.Settings.ProtectedToken);
    }

    [Fact]
    public async Task Stays_logged_in_after_a_restart_without_a_new_login()
    {
        var rig = Create();
        await rig.LogIn();
        using var dir = new TempDir();
        AppSettings.Write(dir.File("settings.json"), new AppSettings { Rapidgator = rig.Settings }.ToJson());

        var restarted = Create(AppSettings.Load(dir.File("settings.json")).Rapidgator);
        Assert.Equal(RapidgatorState.LoggedIn, restarted.Session.State);
        var link = await restarted.Unlock();

        Assert.Equal("Pack.part1.rar", link!.FileName);
        Assert.Equal(12345, link.Size);
        Assert.Equal(1, _fake.Logins);
        Assert.All(_fake.Log, r => Assert.Equal("POST", r.Method));
        Assert.All(_fake.Log, r => Assert.DoesNotContain("?", r.PathAndQuery));
    }

    [Fact]
    public async Task Logout_deletes_password_and_token()
    {
        var rig = Create();
        await rig.LogIn();
        rig.Session.Logout();

        Assert.Equal(RapidgatorState.LoggedOut, rig.Session.State);
        Assert.Null(rig.Settings.ProtectedPassword);
        Assert.Null(rig.Settings.ProtectedToken);
        Assert.Null(rig.Settings.Login);
        Assert.Null(rig.Session.Account);
        await Fails(DebridError.NoKey, () => rig.Session.GetAsync(default));
        Assert.Null(await rig.Unlock());
        Assert.Single(_fake.Log);
    }

    [Fact]
    public async Task Wrong_password_is_one_request_and_never_retried()
    {
        var rig = Create();
        var ex = await Fails(DebridError.BadCredentials, () => rig.LogIn("falsch-falsch"));

        Assert.Equal(1, _fake.Logins);
        Assert.Equal("401", ex.Code);
        Assert.Equal(RapidgatorState.LoggedOut, rig.Session.State);
        Assert.Null(rig.Settings.ProtectedPassword);
        Assert.Contains("Login or password is wrong", ErrorText.Describe(ex)); // „details“ zeigen, wie die Doku empfiehlt
    }

    [Fact]
    public async Task A_rejected_automatic_login_stops_further_logins_even_after_a_restart()
    {
        var rig = Create();
        await rig.LogIn();
        rig.Settings.ProtectedPassword = SecretProtector.Protect("inzwischen-geaendert"); // Passwort beim Dienst geändert
        _fake.ExpireTokens();

        await Fails(DebridError.BadCredentials, () => rig.Unlock());
        Assert.Equal(2, _fake.Logins);
        Assert.Equal(RapidgatorState.LoginFailed, rig.Session.State);
        int after = _fake.Log.Count;

        await Fails(DebridError.BadCredentials, () => rig.Unlock());
        var restarted = Create(rig.Settings);
        await Fails(DebridError.BadCredentials, () => restarted.Unlock());
        Assert.Equal(after, _fake.Log.Count);

        await restarted.LogIn(); // der Benutzer meldet sich selbst neu an
        Assert.NotNull(await restarted.Unlock());
        Assert.Null(restarted.Session.LoginError);
    }

    [Fact]
    public async Task Captcha_or_block_is_login_blocked_with_one_request()
    {
        _fake.Override = r => FakeRapidgator.Fail(401, "Error: Captcha required");
        var rig = Create();
        await Fails(DebridError.LoginBlocked, () => rig.LogIn());
        Assert.Single(_fake.Log);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task A_login_request_is_not_repeated_even_on_overload(int status)
    {
        _fake.Override = r => FakeRapidgator.Fail(status, "Error: busy");
        var rig = Create();
        await Assert.ThrowsAsync<DebridException>(() => rig.LogIn());
        Assert.Single(_fake.Log);
    }

    [Fact]
    public async Task Two_factor_missing_wrong_and_right()
    {
        _fake.TwoFactor = true;
        var rig = Create();

        await Fails(DebridError.TwoFactorRequired, () => rig.LogIn());
        Assert.Equal(RapidgatorState.CodeRequired, rig.Session.State);
        Assert.Null(rig.Settings.ProtectedPassword); // noch nicht angemeldet, nichts gespeichert
        rig.Session.CancelPendingLogin();
        Assert.Equal(RapidgatorState.LoggedOut, rig.Session.State);
        await Fails(DebridError.TwoFactorRequired, () => rig.LogIn());

        await Fails(DebridError.TwoFactorWrong, () => rig.Session.SubmitCodeAsync("000000", default));
        Assert.Equal(3, _fake.Logins);

        await rig.Session.SubmitCodeAsync(Code, default);
        Assert.Equal(RapidgatorState.LoggedIn, rig.Session.State);
        Assert.Equal(4, _fake.Logins);
        string json = Encoding.UTF8.GetString(new AppSettings { Rapidgator = rig.Settings }.ToJson());
        Assert.DoesNotContain(Code, json);
        Assert.True(rig.Settings.TwoFactor);
    }

    [Fact]
    public async Task Expired_token_with_two_factor_asks_for_a_new_code_instead_of_logging_in()
    {
        _fake.TwoFactor = true;
        var rig = Create();
        await rig.LogIn(code: Code);
        _fake.ExpireTokens();

        await Fails(DebridError.TwoFactorRequired, () => rig.Unlock());
        await Fails(DebridError.TwoFactorRequired, () => rig.Unlock());
        Assert.Equal(1, _fake.Logins);
        Assert.Equal(RapidgatorState.CodeRequired, rig.Session.State);

        await rig.Session.SubmitCodeAsync(Code, default);
        Assert.NotNull(await rig.Unlock());
        Assert.Equal(2, _fake.Logins);
    }

    [Fact]
    public async Task Key_file_problems_at_login_keep_nothing()
    {
        foreach (var failure in new Exception[] { new IOException("x"), new UnauthorizedAccessException("x") })
        {
            var rig = Create(protect: _ => throw failure);
            var ex = await Fails(DebridError.KeyUnreadable, () => rig.LogIn());
            Assert.False(rig.Settings.HasCredential);
            Assert.Null(rig.Settings.ProtectedToken);
            Assert.Equal(Loc.T("Error.Rapidgator.KeyUnreadable"), ErrorText.Describe(ex));
        }
    }

    // ---------------------------------------------------------------- Token erneuern

    [Theory]
    [InlineData(401, "Error: Wrong token")]
    [InlineData(404, "Error: User not found")]
    [InlineData(403, "Error: Session expired")]
    public async Task Expired_token_leads_to_exactly_one_new_login(int status, string details)
    {
        var rig = Create();
        await rig.LogIn();
        _fake.Rejection = (status, details);
        _fake.ExpireTokens();

        Assert.NotNull(await rig.Unlock());
        Assert.Equal(2, _fake.Logins);
        Assert.Equal(2, _fake.Count("file/info")); // abgelehnt, dann mit neuem Token
        Assert.Equal(1, _fake.Count("file/download"));
    }

    [Fact]
    public async Task Expiry_at_the_download_request_is_renewed_too()
    {
        var rig = Create();
        await rig.LogIn();
        int downloads = 0;
        _fake.Override = r =>
        {
            if (r.Endpoint == "file/download" && Interlocked.Increment(ref downloads) == 1) _fake.ExpireTokens();
            return null;
        };

        Assert.NotNull(await rig.Unlock());
        Assert.Equal(2, _fake.Logins);
        Assert.Equal(2, _fake.Count("file/download"));
    }

    [Fact]
    public async Task A_new_token_that_is_rejected_at_once_does_not_loop()
    {
        var rig = Create();
        await rig.LogIn();
        _fake.RejectAllTokens = true;

        await Fails(DebridError.SessionExpired, () => rig.Unlock());
        Assert.Equal(2, _fake.Logins);
        Assert.Equal(2, _fake.Count("file/info"));
        int after = _fake.Log.Count;

        for (int i = 0; i < 3; i++) await Fails(DebridError.SessionExpired, () => rig.Unlock());
        Assert.Equal(after, _fake.Log.Count);
        Assert.Equal(RapidgatorState.LoginFailed, rig.Session.State);
    }

    [Fact]
    public async Task Only_one_automatic_login_per_interval()
    {
        var rig = Create();
        await rig.LogIn();
        _fake.ExpireTokens();
        Assert.NotNull(await rig.Unlock()); // erneuert und angenommen
        _fake.ExpireTokens();

        await Fails(DebridError.SessionExpired, () => rig.Unlock());
        await Fails(DebridError.SessionExpired, () => rig.Unlock());
        Assert.Equal(2, _fake.Logins);
    }

    [Fact]
    public async Task Parallel_unlocks_with_an_expired_token_log_in_once()
    {
        var rig = Create();
        await rig.LogIn();
        _fake.ExpireTokens();
        _fake.LoginDelay = TimeSpan.FromMilliseconds(200);

        var links = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => rig.Unlock())));

        Assert.All(links, Assert.NotNull);
        Assert.Equal(2, _fake.Logins);
    }

    [Fact]
    public async Task An_outage_during_the_automatic_login_pauses_instead_of_hammering()
    {
        var rig = Create();
        await rig.LogIn();
        _fake.ExpireTokens();
        _fake.Override = r => r.Endpoint == "user/login" ? FakeRapidgator.Fail(503, "down") : null;

        await Fails(DebridError.ServiceUnavailable, () => rig.Unlock());
        await Fails(DebridError.ServiceUnavailable, () => rig.Unlock());
        Assert.Equal(2, _fake.Logins);
        Assert.Null(rig.Session.LoginError); // keine Ablehnung
    }

    // ---------------------------------------------------------------- Konto, Dateien, Fehler

    [Fact]
    public async Task Traffic_precheck_stops_before_the_download_request()
    {
        _fake.TrafficLeft = "\"1000\"";
        var rig = Create();
        await rig.LogIn();

        var ex = await Fails(DebridError.TrafficExhausted, () => rig.Unlock());

        Assert.Equal(1000, ex.TrafficLeft);
        Assert.Equal(12345, ex.TrafficNeeded);
        Assert.Equal(0, _fake.Count("file/download"));
        Assert.Equal(0, _fake.Count("user/info")); // der Stand aus der Anmeldung genügt
        Assert.Equal(Loc.T("Error.Rapidgator.TrafficLeft", Format.Bytes(1000), Format.Bytes(12345)), ErrorText.Describe(ex));
    }

    [Fact]
    public async Task Old_account_data_is_fetched_again_and_null_traffic_is_not_checked()
    {
        var rig = Create();
        await rig.LogIn();
        rig.Provider.AccountMaxAge = TimeSpan.Zero;
        _fake.TrafficLeft = null;

        Assert.NotNull(await rig.Unlock());
        Assert.Equal(1, _fake.Count("user/info"));
        Assert.Null(rig.Session.Account!.TrafficLeft);
    }

    [Fact]
    public async Task Server_side_traffic_error_is_mapped_and_not_retried()
    {
        var rig = Create();
        await rig.LogIn();
        _fake.Override = r => r.Endpoint == "file/download" ? FakeRapidgator.Fail(403, "Error: You have reached your traffic limit") : null;

        await Fails(DebridError.TrafficExhausted, () => rig.Unlock());
        Assert.Equal(1, _fake.Count("file/download"));
        Assert.Equal(1, _fake.Logins);
    }

    [Fact]
    public async Task Missing_file_is_link_down_and_does_not_log_in_again()
    {
        var rig = Create();
        await rig.LogIn();

        var ex = await Fails(DebridError.LinkDown,
            () => rig.Unlock("https://rapidgator.net/file/ffffffffffffffffffffffffffffffff"));

        Assert.Equal("404", ex.Code);
        Assert.Equal(1, _fake.Logins);
        Assert.Equal("ffffffffffffffffffffffffffffffff", _fake.Log.Last(r => r.Endpoint == "file/info").Form["file_id"]);
    }

    [Fact]
    public async Task Free_account_is_not_premium()
    {
        _fake.Premium = false;
        _fake.TrafficLeft = null;
        var rig = Create();
        var account = await rig.LogIn();
        Assert.False(account.IsPremium);
        Assert.Null(account.PremiumUntil);

        await Fails(DebridError.NotPremium, () => rig.Unlock());
        Assert.Equal(0, _fake.Count("file/download"));
    }

    [Theory]
    [InlineData(429, DebridError.RateLimited)]
    [InlineData(500, DebridError.ServiceUnavailable)]
    public async Task Overload_is_tried_three_times_at_most(int status, DebridError expected)
    {
        var rig = Create();
        await rig.LogIn();
        _fake.Override = r => r.Endpoint == "file/info" ? FakeRapidgator.Fail(status, "busy") : null;

        await Fails(expected, () => rig.Unlock());
        Assert.Equal(3, _fake.Count("file/info"));
    }

    [Fact]
    public async Task One_429_is_retried_successfully()
    {
        var rig = Create();
        await rig.LogIn();
        int n = 0;
        _fake.Override = r => r.Endpoint == "file/info" && Interlocked.Increment(ref n) == 1 ? (429, "slow down") : null;

        Assert.NotNull(await rig.Unlock());
        Assert.Equal(2, _fake.Count("file/info"));
    }

    [Fact]
    public async Task Retries_and_token_renewal_share_the_three_attempts()
    {
        var rig = Create();
        await rig.LogIn();
        int n = 0;
        _fake.Override = r => r.Endpoint != "file/info" ? null : Interlocked.Increment(ref n) switch
        {
            1 => (429, "{}"),
            2 => FakeRapidgator.Fail(401, "Error: Wrong token"),
            _ => (429, "{}"),
        };

        await Fails(DebridError.RateLimited, () => rig.Unlock());
        Assert.Equal(3, _fake.Count("file/info"));
    }

    [Theory]
    [InlineData("<html>nope</html>")]
    [InlineData("{\"status\":200,\"response\":null}")]
    [InlineData("{\"status\":200,\"response\":{}}")]
    [InlineData("{\"status\":200,\"response\":{\"file\":5}}")]
    [InlineData("[1,2]")]
    public async Task Broken_or_incomplete_answers_are_bad_response(string body)
    {
        var rig = Create();
        await rig.LogIn();
        _fake.Override = r => r.Endpoint == "file/info" ? (200, body) : null;

        await Fails(DebridError.BadResponse, () => rig.Unlock());
        Assert.Equal(1, _fake.Count("file/info"));
    }

    [Fact]
    public async Task Redirects_are_not_followed_so_credentials_stay_here()
    {
        _fake.RedirectTo = _fake.BaseUri + "elsewhere";
        var rig = Create();
        await Fails(DebridError.BadResponse, () => rig.LogIn());
        Assert.Single(_fake.Log);
    }

    [Theory]
    [InlineData("http://rapidgator.net/api/v2/")]
    [InlineData("http://example.invalid/api/v2/")]
    public void The_api_refuses_unencrypted_addresses(string address)
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new RapidgatorApi(client, new Uri(address)));
        Assert.StartsWith("https://", RapidgatorApi.DefaultBase);
    }

    // ---------------------------------------------------------------- Download-Adresse und Wartezeit

    [Theory]
    [InlineData("http://pr3.rapidgator.net//?r=download/index&session_id=abc", true)]
    [InlineData("https://rapidgator.net/dl", true)]
    [InlineData("http://rapidgator.net.evil.example/dl", false)]
    [InlineData("http://evilrapidgator.net/dl", false)]
    [InlineData("http://rg.to/dl", false)]
    [InlineData("http://user:pw@pr3.rapidgator.net/dl", false)]
    [InlineData("ftp://pr3.rapidgator.net/dl", false)]
    public async Task Only_rapidgator_hosts_are_accepted_for_the_download(string url, bool accepted)
    {
        _fake.DownloadUrl = url;
        var rig = Create(options: new RapidgatorOptions { BaseUri = _fake.BaseUri });
        await rig.LogIn();

        if (!accepted)
        {
            await Fails(DebridError.BadResponse, () => rig.Unlock());
            return;
        }
        var link = await rig.Unlock();
        Assert.Equal(new Uri(url).AbsoluteUri, link!.Url);
        if (url.StartsWith("http://", StringComparison.Ordinal))
        {
            Assert.StartsWith("https://pr3.rapidgator.net/", link.SecureUrl);
            Assert.EndsWith("session_id=abc", link.SecureUrl);
        }
        else Assert.Null(link.SecureUrl);
        Assert.DoesNotContain("session_id", link.ToString());
    }

    [Fact]
    public async Task The_engine_falls_back_to_the_given_address_when_https_does_not_work()
    {
        using var server = new TestServer(100_000);
        using var dir = new TempDir();
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        int port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var options = new DownloadOptions { RetryDelay = TimeSpan.FromMilliseconds(10) };
        using var engine = new HttpDownloader(options);

        var result = await engine.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin"), ResumeKey: FileUrl,
            SecureUrl: $"https://127.0.0.1:{port}/x"), null, default);

        Assert.Equal(server.Data, File.ReadAllBytes(result.Path));
    }

    [Fact]
    public async Task Delay_is_reported_and_waited_for()
    {
        _fake.Delay = "\"1\"";
        var rig = Create();
        await rig.LogIn();
        var seen = new ConcurrentQueue<int>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await rig.Service.TryUnlockAsync(FileUrl, new Collector(seen), default);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900));
        Assert.Equal(new[] { 1 }, seen);
    }

    private sealed class Collector : IProgress<DebridWait>
    {
        private readonly ConcurrentQueue<int> _seen;
        public Collector(ConcurrentQueue<int> seen) => _seen = seen;
        public void Report(DebridWait value) => _seen.Enqueue(value.SecondsLeft);
    }

    [Fact]
    public async Task Delay_can_be_cancelled_and_has_an_upper_limit()
    {
        _fake.Delay = "300";
        var rig = Create();
        await rig.LogIn();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Unlock(ct: cts.Token));

        _fake.Delay = "999999999";
        await Fails(DebridError.GenerationFailed, () => rig.Unlock());
    }

    // ---------------------------------------------------------------- Online-Check

    [Fact]
    public async Task Check_link_sends_25_per_request_and_no_access_is_not_called_deleted()
    {
        var rig = Create();
        await rig.LogIn();
        var urls = Enumerable.Range(0, 60)
            .Select(i => $"https://rapidgator.net/file/{i:x32}/{(i == 3 ? "noaccess" : "ok")},{i}.bin").ToList();
        var results = new List<LinkInfo>();

        int sent = await rig.Service.CheckLinksAsync(urls, results.AddRange, default);

        Assert.Equal(60, sent);
        Assert.Equal(new[] { 25, 25, 10 },
            _fake.Log.Where(r => r.Endpoint == "file/check_link").Select(r => r.Form["url"].Split(',').Length));
        Assert.Equal(urls, results.Select(r => r.Link));
        Assert.Equal(LinkState.NoAccess, results[3].State);
        Assert.Equal(LinkState.Online, results[4].State);
        Assert.Equal("Checked.bin", results[4].FileName);
        Assert.Equal(77, results[4].Size);

        var candidate = new AddCandidate(urls[3]);
        candidate.UseDebrid("Rapidgator", RapidgatorProvider.ProviderId);
        candidate.ApplyCheck(results[3]);
        Assert.Equal(CheckStatus.NoAccess, candidate.Check);
        Assert.True(candidate.IsChecked);
        Assert.Equal(RapidgatorProvider.ProviderId, candidate.ServiceId);
        Assert.Contains(Loc.T("Candidate.NoAccess"), candidate.StatusText);
        Assert.DoesNotContain(Loc.T("Candidate.Offline"), candidate.StatusText);
    }

    // ---------------------------------------------------------------- Keine Geheimnisse nach außen

    [Theory]
    [InlineData("user/login")]
    [InlineData("file/info")]
    [InlineData("file/download")]
    public async Task Server_details_echoing_secrets_never_reach_exceptions_or_texts(string endpoint)
    {
        var rig = Create();
        if (endpoint != "user/login") await rig.LogIn();
        _fake.Override = r => r.Endpoint != endpoint ? null : FakeRapidgator.Fail(400,
            "Error: bad " + string.Join(" ", r.Form.Values) + " http://pr3.rapidgator.net/?session_id=" + FakeRapidgator.SessionSecret);

        var ex = await Assert.ThrowsAsync<DebridException>(async () =>
        {
            if (endpoint == "user/login") await rig.LogIn(code: Code);
            else await rig.Unlock();
        });

        NoSecrets(ex.ToString() + ErrorText.Describe(ex) + ex.Detail);
    }

    [Theory]
    [InlineData("Error: see https://x.invalid/?session_id=SESSIONSECRET0123456789 now")]
    [InlineData("session_id=SESSIONSECRET0123456789")]
    [InlineData("token = abc and SESSIONSECRET0123456789")]
    public void Details_are_cleaned_of_addresses_and_identifiers(string details)
    {
        string clean = RapidgatorApi.Clean(details) ?? "";
        Assert.DoesNotContain("SESSIONSECRET", clean);
        Assert.DoesNotContain("://", clean);
    }

    [Fact]
    public async Task Connection_errors_carry_no_secrets()
    {
        var rig = Create();
        await rig.LogIn();
        _fake.Dispose();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => rig.Unlock());

        Assert.True(ex is HttpRequestException or TimeoutException, ex.GetType().Name);
        NoSecrets(ex.ToString() + ErrorText.Describe(ex));
    }

    private static void NoSecrets(string text)
    {
        Assert.DoesNotContain("Geheim", text);
        Assert.DoesNotContain("secretTOKEN", text);
        Assert.DoesNotContain(Code, text);
        Assert.DoesNotContain(FakeRapidgator.SessionSecret, text);
        Assert.DoesNotContain("session_id=S", text);
    }

    [Fact]
    public void Every_rapidgator_error_has_a_text_in_every_language()
    {
        var errors = new[]
        {
            DebridError.NoKey, DebridError.KeyUnreadable, DebridError.BadCredentials, DebridError.TwoFactorRequired,
            DebridError.TwoFactorWrong, DebridError.TrafficExhausted, DebridError.SessionExpired, DebridError.LoginBlocked,
            DebridError.NotPremium, DebridError.LinkDown, DebridError.FolderNotSupported, DebridError.HostNotSupported,
            DebridError.GenerationFailed, DebridError.RateLimited, DebridError.ServiceUnavailable, DebridError.BadResponse,
            DebridError.Other, DebridError.Disabled, DebridError.NoLongerSupported, DebridError.HostsUnavailable,
        };
        foreach (var error in errors)
            Assert.NotEmpty(Loc.TextsInAllLanguages("Error.Rapidgator." + error));
        foreach (var error in errors)
            Assert.DoesNotContain('[', ErrorText.Describe(new DebridException(error, "401") { Service = "rapidgator" }));
    }

    // ---------------------------------------------------------------- Vorrang und Zuordnung

    [Fact]
    public async Task Rapidgator_goes_before_alldebrid_and_a_download_keeps_its_service()
    {
        using var allDebrid = new FakeAllDebrid();
        using var http = new HttpClient();
        var debridSettings = new DebridSettings { Enabled = true, ApiKey = FakeAllDebrid.GoodKey };
        var debrid = new DebridService(new AllDebridProvider(http, allDebrid.BaseUri), () => debridSettings);
        await debrid.LoadHostsAsync(false, default);
        var rig = Create();
        var router = new UnlockRouter(rig.Service, debrid);
        var uri = new Uri(FileUrl);
        Assert.True(debrid.Supports(uri)); // AllDebrid kennt den Hoster auch

        // abgemeldet: AllDebrid
        Assert.Equal("alldebrid", (await router.TryUnlockAsync(FileUrl, null, default))!.Service.Provider.Id);
        await Fails(DebridError.Disabled, () => router.TryUnlockAsync(FileUrl, null, default, "rapidgator"));

        await rig.LogIn();
        Assert.Equal("rapidgator", (await router.TryUnlockAsync(FileUrl, null, default))!.Service.Provider.Id);
        Assert.Equal("rapidgator", router.ServiceFor(FileUrl)!.Provider.Id);
        Assert.Equal("alldebrid", (await router.TryUnlockAsync(FileUrl, null, default, "alldebrid"))!.Service.Provider.Id);
        Assert.Equal("alldebrid", router.ServiceFor("https://mega.nz/file/ok")!.Provider.Id);

        // Rapidgator scheitert: kein stilles Ausweichen
        int unlocks = allDebrid.Log.Count;
        await Fails(DebridError.LinkDown,
            () => router.TryUnlockAsync("https://rapidgator.net/file/ffffffffffffffffffffffffffffffff", null, default));
        Assert.Equal(unlocks, allDebrid.Log.Count);

        rig.Settings.Enabled = false;
        Assert.Equal("alldebrid", (await router.TryUnlockAsync(FileUrl, null, default))!.Service.Provider.Id);
        await Fails(DebridError.Disabled, () => router.TryUnlockAsync(FileUrl, null, default, "rapidgator"));
    }

    [Fact]
    public void Old_download_lists_are_migrated()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("downloads.json"),
            "{\"version\":1,\"items\":[{\"id\":\"1\",\"url\":\"https://mega.nz/a\",\"fileName\":\"a\",\"folder\":\"/x\",\"debrid\":true},"
            + "{\"id\":\"2\",\"url\":\"https://mega.nz/b\",\"fileName\":\"b\",\"folder\":\"/x\"}]}");

        var items = new DownloadStore(dir.File("downloads.json")).Load().Items.Select(DownloadItem.FromEntry).ToList();

        Assert.Equal("alldebrid", items[0].ViaService);
        Assert.True(items[0].ViaDebrid);
        Assert.Null(items[1].ViaService);

        var rapidgator = new DownloadItem(FileUrl, "a", "/x") { ViaService = "rapidgator" };
        var entry = rapidgator.ToEntry();
        Assert.False(entry.Debrid);
        Assert.Equal("rapidgator", DownloadItem.FromEntry(entry).ViaService);
    }

    // ---------------------------------------------------------------- Ende zu Ende

    private sealed class ManualTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ManualTimers : ITimerFactory
    {
        public readonly List<ManualTimer> All = new();

        public IUiTimer Create(TimeSpan interval, bool urgent = false)
        {
            var timer = new ManualTimer { Interval = interval };
            All.Add(timer);
            return timer;
        }
    }

    private sealed class QueueContext : SynchronizationContext, IUiDispatcher
    {
        private readonly BlockingCollection<Action> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add(() => d(state));
        public void Post(Action action) => _queue.Add(action);

        public void RunUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!done())
            {
                Assert.True(DateTime.UtcNow < until, "Zeitüberschreitung");
                if (_queue.TryTake(out var action, 20)) action();
            }
        }
    }

    [Fact]
#pragma warning disable xUnit1031 // der Manager braucht seinen eigenen "UI-Thread", daher synchron
    public void End_to_end_with_pause_and_resume()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 300_000 };
        using var dir = new TempDir();
        _fake.FileSize = server.Data.Length;
        _fake.DownloadUrl = server.Url + "?r=download/index&session_id=" + FakeRapidgator.SessionSecret;
        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        var timers = new ManualTimers();
        var manager = new DownloadManager(timers, ui, rapidgator: Options());
        Task.Run(() => manager.Rapidgator.LoginAsync(Login, Password, null, default)).Wait();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var item = new DownloadItem(FileUrl, "Pack.part1.rar", dir.Path);
            var ticker = timers.All[0];

            manager.Add(new[] { item }, start: true);
            ui.RunUntil(() =>
            {
                ticker.Fire();
                return item.DownloadedBytes > 100_000 || item.Status == DownloadStatus.Failed;
            });
            Assert.Equal(DownloadStatus.Running, item.Status);
            Assert.Equal("rapidgator", item.ViaService);
            manager.Pause(new[] { item });
            ui.RunUntil(() => item.Status != DownloadStatus.Running);
            Assert.Equal(DownloadStatus.Paused, item.Status);
            string part = File.ReadAllText(item.TargetPath + ".part.json");
            Assert.DoesNotContain(FakeRapidgator.SessionSecret, part);

            server.ThrottleBytesPerSecond = 0;
            manager.Start(new[] { item });
            ui.RunUntil(() =>
            {
                ticker.Fire();
                return item.Status is DownloadStatus.Completed or DownloadStatus.Failed;
            });

            Assert.True(item.Status == DownloadStatus.Completed, item.Error);
            Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath!));
            Assert.Equal(2, _fake.Count("file/download")); // beim Fortsetzen neu entsperrt
            Assert.Equal(1, _fake.Logins);
            Assert.True(item.TransferredBytes < server.Data.Length * 3L / 2, "nicht von vorn geladen");

            string saved = Encoding.UTF8.GetString(DownloadStore.ToJson(new DownloadList { Items = { item.ToEntry() } }))
                + Encoding.UTF8.GetString(manager.Settings.ToJson())
                + string.Join("\n", item.Log.Select(l => l.Text)) + item.DetailText;
            NoSecrets(saved);
            Assert.DoesNotContain("session_id", saved);

            manager.LogoutRapidgator();
            Assert.False(manager.Settings.Rapidgator.HasCredential);
            Assert.Null(manager.Settings.Rapidgator.ProtectedToken);
        }
        finally
        {
            manager.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        Assert.DoesNotContain("v1:", File.ReadAllText(AppPaths.SettingsFile).Split("\"rapidgator\"")[1].Split('}')[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failing_download_server_leaves_no_session_id_behind(bool notFound)
    {
        using var server = new TestServer(1000) { FailFirstWith503 = 0 };
        using var dir = new TempDir();
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        int port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        // entweder niemand da oder eine Attrappe, die 404 antwortet
        _fake.DownloadUrl = (notFound ? new Uri(_fake.BaseUri, "/gone").AbsoluteUri : $"http://127.0.0.1:{port}/dl")
            + "?r=download/index&session_id=" + FakeRapidgator.SessionSecret;
        _fake.Override = r => r.Endpoint == "/gone" ? (404, "gone") : null;
        var previous = SynchronizationContext.Current;
        var ui = new QueueContext();
        var manager = new DownloadManager(new ManualTimers(), ui, rapidgator: Options());
        manager.Options.MaxRetries = 1;
        manager.Options.RetryDelay = TimeSpan.FromMilliseconds(10);
        Task.Run(() => manager.Rapidgator.LoginAsync(Login, Password, null, default)).Wait();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var item = new DownloadItem(FileUrl, "Pack.part1.rar", dir.Path);
            manager.Add(new[] { item }, start: true);
            ui.RunUntil(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed);

            Assert.Equal(DownloadStatus.Failed, item.Status);
            string seen = item.Error + string.Join("\n", item.Log.Select(l => l.Text))
                + Encoding.UTF8.GetString(DownloadStore.ToJson(new DownloadList { Items = { item.ToEntry() } }));
            NoSecrets(seen);
            Assert.DoesNotContain("session_id", seen);
        }
        finally
        {
            manager.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
#pragma warning restore xUnit1031
}
