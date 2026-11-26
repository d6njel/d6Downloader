using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using D6Downloader.Platform;
using D6Downloader.Engine;

namespace D6Downloader.Browser;

public enum BrowserKind { Chrome, Firefox }

public enum BrowserState
{
    /// <summary>Kein Manifest.</summary>
    NotRegistered,
    /// <summary>Alle Manifeste stimmen mit dieser Programmdatei überein.</summary>
    Registered,
    /// <summary>Ein Manifest besteht, aber nicht alle passen (Programm verschoben, eines fehlt oder ist fremd).</summary>
    Outdated,
}

internal sealed class HostManifest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "stdio";

    [JsonPropertyName("allowed_origins")]
    public string[]? AllowedOrigins { get; set; }

    [JsonPropertyName("allowed_extensions")]
    public string[]? AllowedExtensions { get; set; }
}

[JsonSerializable(typeof(HostManifest))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Verbindet die Browser mit dieser Programmdatei: schreibt das Manifest des Native-Messaging-Hosts in die Ordner, in denen die
/// Browser suchen (<see cref="INativeHostLocations"/>). Das geschieht nur auf Wunsch des Nutzers, nie von selbst.
/// </summary>
public sealed class BrowserSetup
{
    /// <summary>
    /// Feste ID der Chrome-Erweiterung. Sie folgt aus dem Schlüssel <c>key</c> in <c>extension/manifest.json</c> und bleibt so
    /// auf jedem Rechner und in jedem Ordner gleich (ein Test prüft die Übereinstimmung).
    /// </summary>
    public const string ChromeExtensionId = "bdlpheiahljgmcceemlhgakhahbkpmop";

    /// <summary>Feste ID der Firefox-Erweiterung (<c>browser_specific_settings.gecko.id</c>).</summary>
    public const string FirefoxExtensionId = "d6downloader@local";

    private const string Description = "d6Downloader: Links aus dem Browser an die App übergeben";

    private readonly INativeHostLocations _locations;
    private readonly string _exePath;

    public BrowserSetup(INativeHostLocations locations, string exePath)
    {
        _locations = locations;
        _exePath = exePath;
    }

    /// <summary>Alle Manifest-Dateien dieses Browsertyps. Leer, wenn es keinen solchen Browser gibt.</summary>
    public IReadOnlyList<string> ManifestPaths(BrowserKind kind) =>
        _locations.ManifestDirectories(kind).Select(d => Path.Combine(d, NativeHost.ManifestFile)).ToList();

    /// <summary>Inhalt der Manifest-Datei. Nur die eigene Erweiterung darf den Host starten.</summary>
    public string ManifestJson(BrowserKind kind)
    {
        var manifest = new HostManifest { Name = NativeHost.Name, Description = Description, Path = _exePath };
        if (kind == BrowserKind.Chrome) manifest.AllowedOrigins = new[] { $"chrome-extension://{ChromeExtensionId}/" };
        else manifest.AllowedExtensions = new[] { FirefoxExtensionId };
        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.HostManifest);
    }

    public BrowserState GetState(BrowserKind kind)
    {
        var paths = ManifestPaths(kind);
        try
        {
            var present = paths.Where(File.Exists).ToList();
            if (present.Count == 0) return BrowserState.NotRegistered;
            string expected = ManifestJson(kind);
            return present.Count == paths.Count && present.All(p => File.ReadAllText(p) == expected)
                ? BrowserState.Registered : BrowserState.Outdated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BrowserState.Outdated;
        }
    }

    /// <summary>Schreibt die Manifeste (atomar). Auch zum Korrigieren eines verschobenen Ordners.</summary>
    public void Register(BrowserKind kind)
    {
        // Ohne BOM: Firefox verträgt es nicht.
        byte[] json = new System.Text.UTF8Encoding(false).GetBytes(ManifestJson(kind));
        foreach (string path in ManifestPaths(kind))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, json);
        }
    }

    /// <summary>Entfernt die Manifest-Dateien.</summary>
    public void Unregister(BrowserKind kind)
    {
        foreach (string path in ManifestPaths(kind))
            if (File.Exists(path)) File.Delete(path);
    }
}
