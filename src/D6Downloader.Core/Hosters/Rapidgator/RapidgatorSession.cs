using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Debrid;

namespace D6Downloader.Hosters.Rapidgator;

public enum RapidgatorState
{
    LoggedOut,
    /// <summary>Angemeldet. Ein abgelaufenes Token wird bei Bedarf selbst erneuert.</summary>
    LoggedIn,
    /// <summary>Es fehlt ein 2FA-Code: <see cref="RapidgatorSession.SubmitCodeAsync"/>.</summary>
    CodeRequired,
    /// <summary>Die Anmeldung wurde abgelehnt, der Benutzer muss sich neu anmelden (<see cref="RapidgatorSession.LoginError"/>).</summary>
    LoginFailed,
}

/// <summary>
/// Die Anmeldung bei Rapidgator: einmal anmelden, angemeldet bleiben bis zum Abmelden (auch nach einem Neustart).
/// Schleifenschutz: Eine abgelehnte Anmeldung stoppt jeden automatischen Neu-Login; ein automatisch erneuertes Token, das
/// gleich wieder abgelehnt wird, ebenso; sonst höchstens ein automatischer Neu-Login je <see cref="MinReloginInterval"/>.
/// </summary>
public sealed class RapidgatorSession : ICredentialSource
{
    private readonly RapidgatorApi _api;
    private readonly Func<RapidgatorSettings> _settings;
    private readonly Func<string, string> _protect;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _manual = new(1, 1);

    private int _generation;        // Abmelden und neue Anmeldung entwerten, was noch unterwegs ist
    private string? _token;
    private string? _unproven;      // automatisch erneuertes Token, das noch keine Anfrage angenommen hat
    private DateTime? _lastAutoLogin;
    private Task<string>? _relogin; // laufender automatischer Neu-Login, alle Wartenden teilen ihn
    private (DebridError Error, DateTime Until)? _pause;
    private (string Login, string Password)? _pending; // wartet auf den 2FA-Code, nur im Speicher
    private DebridAccount? _account;
    private DateTime _accountAt;

    /// <param name="protect">Nur für Tests, sonst <see cref="SecretProtector.Protect(string)"/>.</param>
    public RapidgatorSession(RapidgatorApi api, Func<RapidgatorSettings> settings, Func<string, string>? protect = null)
    {
        _api = api;
        _settings = settings;
        _protect = protect ?? SecretProtector.Protect;
    }

    public TimeSpan MinReloginInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>So lange wird nach einem Ausfall (nicht erreichbar, 429, 5xx) nicht erneut angemeldet.</summary>
    public TimeSpan OutagePause { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Anmeldung, Status oder gespeicherte Werte haben sich geändert: speichern und anzeigen. Kommt auf beliebigem Thread.
    /// </summary>
    public event Action? Changed;

    public bool HasCredential => _settings().HasCredential;

    /// <summary>Schon ein gespeicherter Zugang zählt: Ist er unbrauchbar, sagt das die Fehlermeldung, statt still auszuweichen.</summary>
    public bool Usable => HasCredential;

    /// <summary>E-Mail der Anmeldung (auch während auf den Code gewartet wird), null = abgemeldet.</summary>
    public string? Login
    {
        get { lock (_lock) return _pending?.Login ?? _settings().Login; }
    }

    public RapidgatorState State
    {
        get
        {
            lock (_lock)
            {
                var s = _settings();
                if (_pending is not null) return RapidgatorState.CodeRequired;
                if (!s.HasCredential) return RapidgatorState.LoggedOut;
                if (Token() is not null) return RapidgatorState.LoggedIn;
                if (s.TwoFactor || s.AutoLoginStopped == DebridError.TwoFactorRequired) return RapidgatorState.CodeRequired;
                return s.AutoLoginStopped is null ? RapidgatorState.LoggedIn : RapidgatorState.LoginFailed;
            }
        }
    }

    /// <summary>Warum kein automatischer Neu-Login mehr stattfindet, null = alles in Ordnung.</summary>
    public DebridError? LoginError
    {
        get { lock (_lock) return _settings().AutoLoginStopped; }
    }

    /// <summary>Zuletzt bekannter Kontostand (Premium bis, Traffic), null = noch nicht abgefragt.</summary>
    public DebridAccount? Account
    {
        get { lock (_lock) return _account; }
    }

    internal DebridAccount? RecentAccount(TimeSpan maxAge)
    {
        lock (_lock) return _account is not null && DateTime.UtcNow - _accountAt < maxAge ? _account : null;
    }

    internal DebridAccount SetAccount(RapidgatorUser user)
    {
        DebridAccount account;
        lock (_lock)
        {
            account = ToAccount(user, _settings().Login);
            _account = account;
            _accountAt = DateTime.UtcNow;
        }
        Notify();
        return account;
    }

    internal void ForgetAccount()
    {
        lock (_lock) _accountAt = default;
    }

    private static DebridAccount ToAccount(RapidgatorUser user, string? login) =>
        new(user.Email ?? login ?? "", user.IsPremium, user.PremiumUntil, user.TrafficLeft, user.TrafficTotal);

    // ---------------------------------------------------------------- Anmelden und Abmelden

    /// <summary>
    /// Anmeldung durch den Benutzer: genau eine Anfrage. Wirft <see cref="DebridException"/> (etwa
    /// <see cref="DebridError.TwoFactorRequired"/>, dann <see cref="SubmitCodeAsync"/>), <see cref="HttpRequestException"/> oder
    /// <see cref="TimeoutException"/>. Bei <see cref="DebridError.KeyUnreadable"/> ließ sich nichts verschlüsselt speichern,
    /// dann wird nichts behalten.
    /// </summary>
    public async Task<DebridAccount> LoginAsync(string login, string password, string? code, CancellationToken ct)
    {
        login = login.Trim();
        code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if (login.Length == 0 || password.Length == 0) throw RapidgatorApi.Error(DebridError.NoKey);

        await _manual.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int generation;
            lock (_lock) generation = ++_generation;
            string token;
            RapidgatorUser user;
            try
            {
                (token, user) = await _api.LoginAsync(login, password, code, ct).ConfigureAwait(false);
            }
            catch (DebridException ex)
            {
                lock (_lock)
                    if (generation == _generation) Rejected(ex.Error, login, password);
                Notify();
                throw;
            }

            string protectedPassword, protectedToken;
            try
            {
                protectedPassword = _protect(password);
                protectedToken = _protect(token);
            }
            catch (Exception ex) when (IsKeyFailure(ex))
            {
                throw RapidgatorApi.Error(DebridError.KeyUnreadable);
            }

            DebridAccount account;
            lock (_lock)
            {
                if (generation != _generation) throw RapidgatorApi.Error(DebridError.NoKey); // inzwischen abgemeldet
                var s = _settings();
                s.Login = login;
                s.ProtectedPassword = protectedPassword;
                s.ProtectedToken = protectedToken;
                s.TwoFactor = code is not null;
                s.AutoLoginStopped = null;
                s.Enabled = true;
                _token = token;
                _unproven = null;
                _pending = null;
                _lastAutoLogin = null;
                _pause = null;
                _account = account = ToAccount(user, login);
                _accountAt = user.Email is null ? default : DateTime.UtcNow;
            }
            Notify();
            return account;
        }
        finally
        {
            _manual.Release();
        }
    }

    private static bool IsKeyFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException;

    /// <summary>Nach einer abgelehnten Anmeldung des Benutzers (unter der Sperre).</summary>
    private void Rejected(DebridError error, string login, string password)
    {
        var s = _settings();
        switch (error)
        {
            case DebridError.TwoFactorRequired:
                _pending = (login, password);
                if (s.HasCredential && string.Equals(s.Login, login, StringComparison.OrdinalIgnoreCase)) s.TwoFactor = true;
                break;
            case DebridError.TwoFactorWrong:
                _pending = (login, password); // der Benutzer darf einen neuen Code eingeben
                if (s.HasCredential) s.AutoLoginStopped = error;
                break;
            case DebridError.BadCredentials or DebridError.LoginBlocked or DebridError.Other:
                _pending = null;
                if (s.HasCredential) s.AutoLoginStopped = error;
                break;
        }
    }

    /// <summary>Den 2FA-Code nachreichen (nach <see cref="RapidgatorState.CodeRequired"/>). Er wird nie gespeichert.</summary>
    public async Task<DebridAccount> SubmitCodeAsync(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) throw RapidgatorApi.Error(DebridError.TwoFactorRequired);
        string login, password;
        lock (_lock)
        {
            if (_pending is { } pending) (login, password) = pending;
            else
            {
                var s = _settings();
                if (!s.HasCredential) throw RapidgatorApi.Error(DebridError.NoKey);
                login = s.Login!;
                password = SecretProtector.Unprotect(s.ProtectedPassword) ?? throw RapidgatorApi.Error(DebridError.KeyUnreadable);
            }
        }
        return await LoginAsync(login, password, code, ct).ConfigureAwait(false);
    }

    /// <summary>Die Code-Eingabe wurde abgebrochen: die vorgemerkte Anmeldung vergessen. Gespeichertes bleibt.</summary>
    public void CancelPendingLogin()
    {
        lock (_lock) _pending = null;
        Notify();
    }

    /// <summary>Löscht Passwort, Token und alles Weitere zur Anmeldung. Danach muss gespeichert werden (<see cref="Changed"/>).</summary>
    public void Logout()
    {
        lock (_lock)
        {
            _generation++;
            var s = _settings();
            s.Enabled = false;
            s.Login = null;
            s.ProtectedPassword = null;
            s.ProtectedToken = null;
            s.TwoFactor = false;
            s.AutoLoginStopped = null;
            _token = _unproven = null;
            _pending = null;
            _lastAutoLogin = null;
            _pause = null;
            _account = null;
            _accountAt = default;
        }
        Notify();
    }

    // ---------------------------------------------------------------- Token

    /// <summary>Unter der Sperre.</summary>
    private string? Token() => _token ??= SecretProtector.Unprotect(_settings().ProtectedToken);

    public Task<string> GetAsync(CancellationToken ct)
    {
        Task<string> login;
        lock (_lock)
        {
            if (!_settings().HasCredential)
                throw RapidgatorApi.Error(_pending is null ? DebridError.NoKey : DebridError.TwoFactorRequired);
            if (Token() is { } token) return Task.FromResult(token);
            // Task.Run: Das finally räumt sicher erst nach dieser Sperre auf.
            login = _relogin ??= Task.Run(AutoLoginAsync);
        }
        return login.WaitAsync(ct);
    }

    public async Task<bool> RefreshAfterRejectAsync(string rejected, CancellationToken ct)
    {
        bool dropped = false, loop = false;
        lock (_lock)
        {
            var s = _settings();
            if (!s.HasCredential) throw RapidgatorApi.Error(DebridError.NoKey);
            string? current = Token();
            if (current is not null && current != rejected) return true; // ein anderer Aufruf hat schon erneuert
            if (current is not null)
            {
                _token = null;
                s.ProtectedToken = null;
                dropped = true;
                if (_unproven == rejected)
                {
                    // Gerade erst erneuert und wieder abgelehnt: Ein weiterer Login brächte dasselbe.
                    s.AutoLoginStopped = DebridError.SessionExpired;
                    loop = true;
                }
                _unproven = null;
            }
        }
        if (dropped) Notify();
        if (loop) return false;
        await GetAsync(ct).ConfigureAwait(false);
        return true;
    }

    internal void MarkAccepted(string token)
    {
        lock (_lock)
            if (_unproven == token) _unproven = null;
    }

    /// <summary>
    /// Der eine automatische Neu-Login. Er hat nur sein eigenes Zeitlimit: Bricht ein Download ab, warten die anderen weiter.
    /// </summary>
    private async Task<string> AutoLoginAsync()
    {
        bool notify = false;
        try
        {
            int generation;
            string login, password;
            lock (_lock)
            {
                generation = _generation;
                var s = _settings();
                if (Token() is { } existing) return existing;
                if (!s.HasCredential) throw RapidgatorApi.Error(DebridError.NoKey);
                if (s.TwoFactor)
                {
                    notify = true;
                    throw RapidgatorApi.Error(DebridError.TwoFactorRequired);
                }
                if (s.AutoLoginStopped is { } stopped) throw RapidgatorApi.Error(stopped);
                if (_pause is { } pause && DateTime.UtcNow < pause.Until) throw RapidgatorApi.Error(pause.Error);
                if (_lastAutoLogin is { } last && DateTime.UtcNow - last < MinReloginInterval)
                    throw RapidgatorApi.Error(DebridError.SessionExpired);
                login = s.Login!;
                password = SecretProtector.Unprotect(s.ProtectedPassword) ?? throw RapidgatorApi.Error(DebridError.KeyUnreadable);
                _lastAutoLogin = DateTime.UtcNow;
            }

            string token;
            RapidgatorUser user;
            try
            {
                (token, user) = await _api.LoginAsync(login, password, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                           || ex is DebridException { Error: DebridError.RateLimited
                                               or DebridError.ServiceUnavailable or DebridError.BadResponse })
            {
                // Ausfall, keine Ablehnung: später darf es noch einmal versucht werden.
                lock (_lock)
                {
                    _lastAutoLogin = null;
                    _pause = ((ex as DebridException)?.Error ?? DebridError.ServiceUnavailable, DateTime.UtcNow + OutagePause);
                }
                throw;
            }
            catch (DebridException ex)
            {
                lock (_lock)
                {
                    if (generation == _generation)
                    {
                        var s = _settings();
                        if (ex.Error == DebridError.TwoFactorRequired) s.TwoFactor = true;
                        s.AutoLoginStopped = ex.Error;
                        notify = true;
                    }
                }
                throw;
            }

            lock (_lock)
            {
                if (generation != _generation) throw RapidgatorApi.Error(DebridError.NoKey);
                var s = _settings();
                _token = _unproven = token;
                try { s.ProtectedToken = _protect(token); }
                catch (Exception ex) when (IsKeyFailure(ex)) { s.ProtectedToken = null; } // dann gilt es bis zum Programmende
                if (user.Email is not null)
                {
                    _account = ToAccount(user, login);
                    _accountAt = DateTime.UtcNow;
                }
                notify = true;
            }
            return token;
        }
        finally
        {
            lock (_lock) _relogin = null;
            if (notify) Notify();
        }
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { /* ein fehlerhafter Beobachter darf die Anmeldung nicht stören */ }
    }
}
