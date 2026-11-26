using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using D6Downloader.Engine;

namespace D6Downloader.Remote;

internal sealed class ReplayState
{
    /// <summary>Server-Zeit (Sekunden) der zuletzt verarbeiteten Nachricht.</summary>
    public long? LastTime { get; set; }

    /// <summary>Nach einer beschädigten Datei: Älteres (Unix-ms) wird nicht mehr angenommen.</summary>
    public long NotBefore { get; set; }

    public Dictionary<string, List<SeenId>> Devices { get; set; } = new();
}

internal sealed class SeenId
{
    public string Id { get; set; } = "";
    public long Ts { get; set; }
}

[JsonSerializable(typeof(ReplayState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ReplayJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Gesehene Nachrichten-IDs über Neustarts (<c>remote-state.json</c>, nur für den Benutzer lesbar). Gespeichert wird vor der
/// Verarbeitung: lieber eine Nachricht verlieren als doppelt starten. Lässt sich nicht speichern, wird nichts angenommen.
/// </summary>
public sealed class ReplayStore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(13);
    public static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(10);
    internal const int MaxPerDevice = 5000;

    private readonly Func<string> _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly ReplayState _state;
    private readonly bool _locked;

    /// <param name="path">Als Funktion: Der Datenordner kann wechseln (Portable-Modus).</param>
    public ReplayStore(Func<string> path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var result = DataFile.Load(path(), ReplayJsonContext.Default.ReplayState, out var state);
        _state = state ?? new ReplayState();
        _state.Devices ??= new();
        _locked = result == DataFileState.Locked;
        if (result == DataFileState.Corrupt) _state.NotBefore = _clock().ToUnixTimeMilliseconds();
    }

    public long? LastTime
    {
        get { lock (_gate) return _state.LastTime; }
    }

    /// <summary>True genau einmal je Gerät und ID, und nur im Zeitfenster [jetzt − 13 h, jetzt + 10 min].</summary>
    public bool TryAccept(string deviceId, string id, long timestampMs, long serverTime)
    {
        lock (_gate)
        {
            if (_locked) return false;
            var now = _clock();
            long oldest = (now - MaxAge).ToUnixTimeMilliseconds();
            if (timestampMs < oldest || timestampMs > (now + MaxFuture).ToUnixTimeMilliseconds() || timestampMs < _state.NotBefore)
                return false;
            Prune(oldest);
            if (!_state.Devices.TryGetValue(deviceId, out var seen)) _state.Devices[deviceId] = seen = new();
            if (seen.Exists(s => s.Id == id)) return false;
            var entry = new SeenId { Id = id, Ts = timestampMs };
            seen.Add(entry);
            if (seen.Count > MaxPerDevice) seen.RemoveRange(0, seen.Count - MaxPerDevice);
            long? previous = _state.LastTime;
            if (serverTime > (previous ?? 0)) _state.LastTime = serverTime;
            if (Save()) return true;
            seen.Remove(entry);
            _state.LastTime = previous;
            return false;
        }
    }

    internal int Count(string deviceId)
    {
        lock (_gate) return _state.Devices.TryGetValue(deviceId, out var seen) ? seen.Count : 0;
    }

    public void Forget(string deviceId)
    {
        lock (_gate)
        {
            if (!_locked && _state.Devices.Remove(deviceId)) Save();
        }
    }

    /// <summary>Den Stand neu schreiben, etwa nachdem der Datenordner gewechselt hat.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_locked) Save();
        }
    }

    private void Prune(long oldest)
    {
        foreach (var seen in _state.Devices.Values) seen.RemoveAll(s => s.Ts < oldest);
    }

    private bool Save()
    {
        try
        {
            string path = _path();
            // Die Zwischendatei schon mit 0600 anlegen, AtomicFile behält die Rechte.
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.Delete(path + ".tmp");
            using (new FileStream(path + ".tmp", options)) { }
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(_state, ReplayJsonContext.Default.ReplayState));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
