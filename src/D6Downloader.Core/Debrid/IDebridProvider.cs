using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Debrid;

/// <summary>Ein Debrid-Dienst. Alles Dienstspezifische (Adressen, Antwortformat, Fehlercodes) bleibt hinter dieser Schnittstelle.</summary>
public interface IDebridProvider
{
    string Name { get; }

    /// <summary>Feste Kennung, so steht sie in <c>downloads.json</c>.</summary>
    string Id => Name.ToLowerInvariant();

    /// <summary>Fest bekannte Hoster, die ohne Abfrage gelten. Null = Liste vom Dienst (<see cref="GetHostsAsync"/>).</summary>
    IReadOnlyList<string>? FixedHosts => null;

    /// <summary>Kann der Dienst mit diesem Link eines bekannten Hosters etwas anfangen (etwa keine Info-Seite)?</summary>
    bool Handles(Uri uri) => true;

    /// <summary>So viele Links gehen in eine Anfrage des Online-Checks.</summary>
    int CheckBatchSize => DebridService.CheckBatchSize;

    /// <summary>Prüft den API-Key und liefert das Konto. Wirft <see cref="DebridException"/> (etwa <see cref="DebridError.BadKey"/>).</summary>
    Task<DebridAccount> CheckAccountAsync(string apiKey, CancellationToken ct);

    /// <summary>
    /// Entsperrt einen Hoster-Link zu einem direkten Download-Link. Muss der Dienst den Link erst erzeugen („delayed“),
    /// wartet die Methode darauf und meldet den Stand über <paramref name="waiting"/>.
    /// </summary>
    Task<DebridLink> UnlockAsync(string apiKey, string link, string? password, IProgress<DebridWait>? waiting,
        CancellationToken ct);

    /// <summary>Domains der unterstützten Hoster (ohne führendes <c>www.</c>, klein geschrieben). Braucht keinen Key.</summary>
    Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken ct);

    /// <summary>
    /// Online-Check: fragt Name, Größe und Erreichbarkeit der Links ab, ohne sie zu entsperren. Liefert zu jedem Link genau einen
    /// Eintrag in der Reihenfolge der Eingabe; was der Dienst nicht beantwortet, steht auf <see cref="LinkState.Unknown"/>.
    /// </summary>
    Task<IReadOnlyList<LinkInfo>> CheckLinksAsync(string apiKey, IReadOnlyList<string> links, CancellationToken ct);
}

public enum LinkState
{
    /// <summary>Der Dienst konnte (noch) nichts sagen: Hoster nicht unterstützt, vorübergehend nicht erreichbar, keine Antwort.</summary>
    Unknown,
    Online,
    /// <summary>Datei beim Hoster gelöscht oder nicht erreichbar.</summary>
    Offline,
    /// <summary>Der Link ist passwortgeschützt (Name und Größe fehlen dann meist).</summary>
    PasswordProtected,
    /// <summary>Der Hoster meldet „kein Zugriff“. Ob die Datei gelöscht ist, sagt das nicht.</summary>
    NoAccess,
}

/// <param name="Link">Der abgefragte Link, unverändert.</param>
/// <param name="FileName">Dateiname laut Dienst, Null = keiner genannt.</param>
/// <param name="Size">Größe in Bytes, -1 = unbekannt.</param>
public sealed record LinkInfo(string Link, LinkState State, string? FileName, long Size);

/// <param name="PremiumUntil">Null, wenn das Konto nicht (mehr) Premium ist.</param>
/// <param name="TrafficLeft">Verbleibende Bytes, null = unbekannt oder ohne Grenze.</param>
public sealed record DebridAccount(string UserName, bool IsPremium, DateTimeOffset? PremiumUntil,
    long? TrafficLeft = null, long? TrafficTotal = null);

/// <param name="Url">Direkter Download-Link (meist nur begrenzt gültig).</param>
/// <param name="FileName">Dateiname laut Dienst, Null = keiner genannt.</param>
/// <param name="Size">Größe in Bytes, -1 = unbekannt.</param>
/// <param name="SecureUrl">Dieselbe Datei über HTTPS, wird zuerst versucht. Null = nur <paramref name="Url"/>.</param>
public sealed record DebridLink(string Url, string? FileName, long Size, string? Host, string? SecureUrl = null)
{
    /// <summary>Ohne die Adressen: Sie können eine Sitzungskennung tragen.</summary>
    public override string ToString() => $"DebridLink {{ FileName = {FileName}, Size = {Size}, Host = {Host} }}";
}

/// <summary>Der Dienst erzeugt den Link noch. <paramref name="SecondsLeft"/> ist die Schätzung des Dienstes, -1 = unbekannt.</summary>
public sealed record DebridWait(int SecondsLeft);

public enum DebridError
{
    /// <summary>Kein Key eingetragen.</summary>
    NoKey,
    /// <summary>Ein Key ist gespeichert, lässt sich auf diesem Windows-Konto aber nicht entschlüsseln.</summary>
    KeyUnreadable,
    /// <summary>Der Dienst kennt den Key nicht.</summary>
    BadKey,
    /// <summary>Der Key enthält Zeichen, die ein Key nicht hat (Leerraum, Nicht-ASCII). Er wurde nicht gesendet.</summary>
    KeyFormat,
    /// <summary>Konto gesperrt oder Key blockiert.</summary>
    Blocked,
    /// <summary>Kein Premium-Konto (oder Testkontingent aufgebraucht).</summary>
    NotPremium,
    /// <summary>Der Hoster wird nicht unterstützt.</summary>
    HostNotSupported,
    /// <summary>Hoster gerade nicht erreichbar, voll oder Tageslimit erreicht: später noch einmal.</summary>
    HostUnavailable,
    /// <summary>Datei beim Hoster gelöscht oder nicht erreichbar.</summary>
    LinkDown,
    /// <summary>Passwortgeschützter Link.</summary>
    PasswordRequired,
    /// <summary>Der Dienst erzeugt den Link nicht (delayed fehlgeschlagen) oder zu langsam.</summary>
    GenerationFailed,
    /// <summary>Zu viele Anfragen.</summary>
    RateLimited,
    /// <summary>Wartung oder interner Fehler beim Dienst.</summary>
    ServiceUnavailable,
    /// <summary>Antwort nicht lesbar oder ohne erwartete Felder.</summary>
    BadResponse,
    /// <summary>Anderer Fehler des Dienstes, <see cref="DebridException.Code"/> nennt ihn.</summary>
    Other,
    /// <summary>Der Download wurde über den Dienst angelegt, der Dienst ist aber ausgeschaltet.</summary>
    Disabled,
    /// <summary>Der Download wurde über den Dienst angelegt, der Dienst übernimmt den Hoster aber nicht (mehr).</summary>
    NoLongerSupported,
    /// <summary>Der Download wurde über den Dienst angelegt, die Liste der Hoster ist aber gerade nicht zu bekommen.</summary>
    HostsUnavailable,
    /// <summary>E-Mail oder Passwort falsch.</summary>
    BadCredentials,
    /// <summary>Das Konto verlangt einen 2FA-Code.</summary>
    TwoFactorRequired,
    /// <summary>Der 2FA-Code ist falsch.</summary>
    TwoFactorWrong,
    /// <summary>Der Traffic des Kontos reicht nicht.</summary>
    TrafficExhausted,
    /// <summary>Die Anmeldung ist abgelaufen und ließ sich nicht erneuern.</summary>
    SessionExpired,
    /// <summary>Anmeldung gesperrt (Captcha, Bestätigung, Konto oder IP gesperrt).</summary>
    LoginBlocked,
    /// <summary>Ordner-Links werden nicht unterstützt.</summary>
    FolderNotSupported,
}

/// <summary>
/// Fehler eines Debrid-Dienstes. Die Meldung ist Entwicklertext ohne Key und ohne Link, die Anzeige baut ihren Text aus
/// <see cref="Error"/> (siehe <c>ErrorText</c>).
/// </summary>
public sealed class DebridException : Exception
{
    public DebridException(DebridError error, string? code = null, string? message = null)
        : base(message ?? $"Debrid: {error}" + (code is null ? "" : $" ({code})"))
    {
        Error = error;
        Code = code;
    }

    public DebridError Error { get; }

    /// <summary>Fehlercode des Dienstes, etwa <c>LINK_DOWN</c>. Null, wenn der Fehler nicht vom Dienst kam.</summary>
    public string? Code { get; }

    /// <summary>Kennung des Dienstes (<see cref="IDebridProvider.Id"/>), null = AllDebrid.</summary>
    public string? Service { get; set; }

    /// <summary>Bereinigte Erklärung des Servers für die Anzeige, ohne Geheimnisse und Adressen.</summary>
    public string? Detail { get; init; }

    /// <summary>Bei der Traffic-Vorprüfung: verbleibende und benötigte Bytes, sonst -1.</summary>
    public long TrafficLeft { get; init; } = -1;

    public long TrafficNeeded { get; init; } = -1;
}
