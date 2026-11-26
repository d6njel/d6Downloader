using System;

namespace D6Downloader.Power;

public enum PowerAction
{
    None,
    Shutdown,
    Hibernate,
    /// <summary>Energie sparen (Standby).</summary>
    Sleep,
}

/// <summary>
/// Löst die Aktion des Systems aus. Die echte Umsetzung hängt nur die App ein: Tests und
/// Werkzeuge bekommen vom <c>DownloadManager</c> die wirkungslose <see cref="NoPowerActions"/>.
/// </summary>
public interface IPowerActions
{
    /// <summary>Kann das System diese Aktion gerade ausführen (etwa Ruhezustand nur, wenn er eingeschaltet ist)?</summary>
    bool IsAvailable(PowerAction action);

    /// <summary>Führt die Aktion aus. Liefert eine Fehlermeldung oder null.</summary>
    string? Execute(PowerAction action);
}

/// <summary>Tut nichts. Vorgabe, solange niemand die echten Aktionen einhängt.</summary>
public sealed class NoPowerActions : IPowerActions
{
    public static readonly NoPowerActions Instance = new();

    public bool IsAvailable(PowerAction action) => action != PowerAction.None;

    public string? Execute(PowerAction action) => null;
}

public static class PowerText
{
    /// <summary>Kurzname für Auswahl und Statusleiste.</summary>
    public static string Name(PowerAction action) => action switch
    {
        PowerAction.Shutdown => Loc.T("Power.Shutdown"),
        PowerAction.Hibernate => Loc.T("Power.Hibernate"),
        PowerAction.Sleep => Loc.T("Power.Sleep"),
        _ => Loc.T("Power.Nothing"),
    };

    /// <summary>Satz für den Countdown.</summary>
    public static string Sentence(PowerAction action) => action switch
    {
        PowerAction.Shutdown => Loc.T("Power.Sentence.Shutdown"),
        PowerAction.Hibernate => Loc.T("Power.Sentence.Hibernate"),
        PowerAction.Sleep => Loc.T("Power.Sentence.Sleep"),
        _ => "",
    };
}
