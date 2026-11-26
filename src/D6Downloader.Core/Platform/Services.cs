using System.Collections.Generic;
using System.IO;
using D6Downloader.Browser;

namespace D6Downloader.Platform;

/// <summary>Liefert den Hauptschlüssel (32 Byte) für <c>SecretProtector</c>. Darf IOException werfen.</summary>
public interface ISecretKeyProvider
{
    byte[] GetKey();
}

/// <summary>Hält das System wach, solange etwas läuft. Immer vom UI-Thread aufgerufen, auch mit unverändertem Wert.</summary>
public interface IKeepAwake
{
    void Set(bool on);
}

/// <summary>Tut nichts. Vorgabe, solange niemand die echte Sperre einhängt.</summary>
public sealed class NoKeepAwake : IKeepAwake
{
    public static readonly NoKeepAwake Instance = new();

    public void Set(bool on) { }
}

/// <summary>Papierkorb der Arbeitsumgebung.</summary>
public interface ITrash
{
    /// <summary>Verschiebt die Datei in den Papierkorb. Wirft IOException, wenn das nicht geht, und löscht nie endgültig.</summary>
    void MoveToTrash(string path);
}

/// <summary>Kein Papierkorb: wirft immer. Vorgabe, solange keine Plattform eingehängt ist.</summary>
public sealed class NoTrash : ITrash
{
    public static readonly NoTrash Instance = new();

    public void MoveToTrash(string path) => throw new IOException("Kein Papierkorb verfügbar.");
}

/// <summary>Start der App beim Anmelden.</summary>
public interface IAutostart
{
    bool IsEnabled();
    void Set(bool enabled);
}

/// <summary>Ordner, in denen die Browser Manifeste für Native Messaging suchen. Austauschbar, damit Tests nie echte anfassen.</summary>
public interface INativeHostLocations
{
    /// <summary>Nur Ordner von Browsern, die es für den Benutzer gibt. Leer, wenn keiner da ist.</summary>
    IReadOnlyList<string> ManifestDirectories(BrowserKind kind);
}

/// <summary>Dateien, Ordner und Adressen mit dem Programm der Arbeitsumgebung öffnen.</summary>
public interface IShell
{
    void Open(string pathOrUrl);
    void ShowInFolder(string path);
}

public interface IFreeSpace
{
    /// <summary>
    /// Freier Platz für den Benutzer auf dem Dateisystem, auf dem der Ordner liegt (gibt es ihn noch nicht, zählt der nächste
    /// vorhandene darüber). Null, wenn nicht ermittelbar.
    /// </summary>
    long? Query(string directory);
}

/// <summary>Sucht die Programme zum Entpacken. In <paramref name="forbiddenDirs"/> wird nie eines gestartet.</summary>
public interface IToolLocator
{
    string? FindSevenZip(string? configured, IEnumerable<string> forbiddenDirs);
    string? FindTar(IEnumerable<string> forbiddenDirs);
    string? FindUnar(IEnumerable<string> forbiddenDirs);
    string? FindLsar(IEnumerable<string> forbiddenDirs);
}

/// <summary>Findet nichts. Vorgabe, solange keine Plattform eingehängt ist.</summary>
public sealed class NoToolLocator : IToolLocator
{
    public static readonly NoToolLocator Instance = new();

    public string? FindSevenZip(string? configured, IEnumerable<string> forbiddenDirs) => null;
    public string? FindTar(IEnumerable<string> forbiddenDirs) => null;
    public string? FindUnar(IEnumerable<string> forbiddenDirs) => null;
    public string? FindLsar(IEnumerable<string> forbiddenDirs) => null;
}

/// <summary>Die Anzeigesprachen des Benutzers (etwa <c>de-DE</c>), die wichtigste zuerst. Leer, wenn unbekannt.</summary>
public interface IUserLanguages
{
    IReadOnlyList<string> Get();
}

public sealed class NoUserLanguages : IUserLanguages
{
    public static readonly NoUserLanguages Instance = new();

    public IReadOnlyList<string> Get() => System.Array.Empty<string>();
}

/// <summary>Was an einer <see cref="IOException"/> lokal schiefging.</summary>
public enum IoErrorKind
{
    /// <summary>Kein erkennbarer Fehler des Dateisystems (etwa eine abgebrochene Verbindung).</summary>
    NotLocal,
    /// <summary>Ordner oder Laufwerk sind nicht (mehr) da.</summary>
    TargetGone,
    PathTooLong,
    NoAccess,
    /// <summary>Anderer Fehler des Dateisystems.</summary>
    Other,
}

public interface IIoErrorClassifier
{
    IoErrorKind Classify(IOException ex);
}

public sealed class NoIoErrorClassifier : IIoErrorClassifier
{
    public static readonly NoIoErrorClassifier Instance = new();

    public IoErrorKind Classify(IOException ex) => IoErrorKind.NotLocal;
}
