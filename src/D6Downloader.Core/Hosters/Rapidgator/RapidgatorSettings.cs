using System.Text.Json.Serialization;
using D6Downloader.Debrid;

namespace D6Downloader.Hosters.Rapidgator;

/// <summary>
/// Rapidgator in <c>settings.json</c>. Passwort und Token stehen nur verschlüsselt darin (<see cref="SecretProtector"/>),
/// der 2FA-Code nie. Geschrieben werden die Felder nur von <see cref="RapidgatorSession"/>.
/// </summary>
public sealed class RapidgatorSettings
{
    /// <summary>Rapidgator-Links über das eigene Konto laden.</summary>
    public bool Enabled { get; set; }

    /// <summary>E-Mail-Adresse des Kontos.</summary>
    public string? Login { get; set; }

    public string? ProtectedPassword { get; set; }
    public string? ProtectedToken { get; set; }

    /// <summary>Die Anmeldung brauchte einen 2FA-Code: kein automatischer Neu-Login, der Code wird neu erfragt.</summary>
    public bool TwoFactor { get; set; }

    /// <summary>
    /// Gesetzt, wenn eine Anmeldung abgelehnt wurde: kein automatischer Neu-Login mehr, bis der Benutzer sich selbst
    /// erfolgreich anmeldet (auch nach einem Neustart, sonst droht die IP-Sperre).
    /// </summary>
    public DebridError? AutoLoginStopped { get; set; }

    [JsonIgnore]
    public bool HasCredential => !string.IsNullOrEmpty(Login) && !string.IsNullOrEmpty(ProtectedPassword);
}
