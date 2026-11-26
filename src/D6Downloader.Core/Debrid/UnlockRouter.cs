using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Debrid;

/// <summary>Ein entsperrter Link und der Dienst, der ihn geliefert hat.</summary>
public sealed record UnlockedLink(DebridLink Link, DebridService Service);

/// <summary>
/// Verteilt Links auf mehrere Dienste. Die Reihenfolge ist die Priorität: zuerst das eigene Konto beim Hoster (Rapidgator),
/// dann der Debrid-Dienst. Ein Download, der schon über einen Dienst lief, bleibt bei diesem (<c>requiredId</c>), er wechselt
/// nie still zu einem anderen.
/// </summary>
public sealed class UnlockRouter
{
    private readonly DebridService[] _services;

    public UnlockRouter(params DebridService[] services) => _services = services;

    public IReadOnlyList<DebridService> Services => _services;

    public DebridService? Find(string id) => Array.Find(_services, s => s.Provider.Id == id);

    /// <summary>Lädt die Hoster-Listen im Hintergrund, soweit nötig. Das Task scheitert nie.</summary>
    public Task EnsureHosts() => Task.WhenAll(_services.Select(s => s.EnsureHosts()));

    /// <summary>Ja, wenn ein Dienst den Hoster kennt, nein, wenn alle Listen da sind und keiner ihn kennt, sonst null.</summary>
    public bool? Supports(Uri uri)
    {
        bool unknown = false;
        foreach (var service in _services)
        {
            bool? supported = service.Supports(uri);
            if (supported == true) return true;
            unknown |= supported is null;
        }
        return unknown ? null : false;
    }

    /// <summary>Der Dienst, der den Link beim Start entsperren würde, sonst null.</summary>
    public DebridService? ServiceFor(string url) => Array.Find(_services, s => s.WillUnlock(url));

    public bool WillUnlock(string url) => ServiceFor(url) is not null;

    /// <summary>Wie <see cref="DebridService.TryUnlockAsync"/>. Null heißt: normal herunterladen.</summary>
    /// <param name="requiredId">Der Download wurde über diesen Dienst angelegt: nur er darf ihn entsperren, kein Null.</param>
    public async Task<UnlockedLink?> TryUnlockAsync(string url, IProgress<DebridWait>? waiting, CancellationToken ct,
        string? requiredId = null)
    {
        if (requiredId is not null)
        {
            var service = Find(requiredId) ?? throw new DebridException(DebridError.Disabled);
            var link = await service.TryUnlockAsync(url, waiting, ct, required: true).ConfigureAwait(false);
            return new UnlockedLink(link!, service);
        }
        foreach (var service in _services)
        {
            var link = await service.TryUnlockAsync(url, waiting, ct).ConfigureAwait(false);
            if (link is not null) return new UnlockedLink(link, service);
        }
        return null;
    }

    /// <summary>
    /// Online-Check, jeder Link beim zuständigen Dienst. Scheitert ein Dienst, werden die anderen trotzdem gefragt und danach
    /// wird der erste Fehler geworfen.
    /// </summary>
    public async Task<int> CheckLinksAsync(IEnumerable<string> urls, Action<IReadOnlyList<LinkInfo>> onResults,
        CancellationToken ct)
    {
        int sent = 0;
        ExceptionDispatchInfo? failed = null;
        foreach (var group in urls.GroupBy(ServiceFor).Where(g => g.Key is not null).ToList())
        {
            try
            {
                sent += await group.Key!.CheckLinksAsync(group, onResults, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        failed?.Throw();
        return sent;
    }
}
