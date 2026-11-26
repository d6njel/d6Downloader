using System;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Debrid;

/// <summary>
/// Woher ein Dienst seinen Zugang nimmt: bei AllDebrid der feste API-Key aus den Einstellungen, bei Rapidgator das Token der
/// Anmeldung, das sich erneuern lässt.
/// </summary>
public interface ICredentialSource
{
    /// <summary>Es ist ein Zugang gespeichert, ob brauchbar oder nicht.</summary>
    bool HasCredential { get; }

    /// <summary>Der Dienst gilt damit als aktiv (sofern er eingeschaltet ist).</summary>
    bool Usable { get; }

    /// <summary>Liefert Key oder Token. Wirft <see cref="DebridException"/>, wenn es keinen gibt.</summary>
    Task<string> GetAsync(CancellationToken ct);

    /// <summary>
    /// Der Dienst hat <paramref name="rejected"/> abgelehnt. Wahr: Ein neuer Versuch mit <see cref="GetAsync"/> lohnt.
    /// </summary>
    Task<bool> RefreshAfterRejectAsync(string rejected, CancellationToken ct);
}

/// <summary>Der API-Key aus den Debrid-Einstellungen. Er lässt sich nicht erneuern.</summary>
public sealed class StaticKeySource : ICredentialSource
{
    private readonly Func<DebridSettings> _settings;

    public StaticKeySource(Func<DebridSettings> settings) => _settings = settings;

    public bool HasCredential => _settings().HasKey;
    public bool Usable => _settings().KeyUsable;

    public Task<string> GetAsync(CancellationToken ct)
    {
        var settings = _settings();
        return Task.FromResult(settings.ApiKey
            ?? throw new DebridException(settings.HasKey ? DebridError.KeyUnreadable : DebridError.NoKey));
    }

    public Task<bool> RefreshAfterRejectAsync(string rejected, CancellationToken ct) => Task.FromResult(false);
}
