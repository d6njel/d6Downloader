using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Downloads;

namespace D6Downloader.Remote;

/// <summary>Ein gekoppeltes Handy, wie der Empfänger es braucht.</summary>
public sealed record ReceiverDevice(string Name, byte[] Seed, DateTimeOffset Paired)
{
    public RemoteKeys Keys { get; } = RemoteCrypto.Derive(Seed);
    public string DeviceId => Keys.DeviceId;
}

/// <summary>Eine angenommene Link-Nachricht.</summary>
public sealed record IncomingLinks(string DeviceId, string DeviceName, string Id, IReadOnlyList<string> Links);

public enum RelayStatus
{
    Off,
    /// <summary>Eingeschaltet, aber kein Handy gekoppelt: keine Verbindung.</summary>
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    RateLimited,
}

public sealed class RelayReceiverOptions
{
    public string PcName { get; init; } = Environment.MachineName;
    public string Version { get; init; } = "";
    /// <summary>Der aktuelle Modus, <see cref="ReceiveMode"/>.</summary>
    public Func<string> Mode { get; init; } = () => ReceiveMode.Dialog;
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Hält eine Verbindung zum Vermittler für alle Handys, entschlüsselt, prüft (Replay, Grenzen, Flut) und quittiert.
/// Die Ereignisse kommen auf einem Hintergrund-Thread.
/// </summary>
public sealed class RelayReceiver : IDisposable
{
    internal const int MaxLinks = 50, MaxLinkMessages = 30, MaxInvalid = 30, MaxPerMinute = 200;
    private static readonly TimeSpan LimitWindow = TimeSpan.FromMinutes(10);
    private static readonly int[] Backoff = { 2, 5, 10, 30, 60, 120 };

    private readonly IRelay _relay;
    private readonly ReplayStore _replay;
    private readonly RelayReceiverOptions _options;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ReceiverDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _invalid = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _linkTimes = new();
    private readonly Dictionary<string, DateTimeOffset> _limitReplies = new();
    private readonly HashSet<string> _floodReported = new();
    private PairingSession? _pairing;
    private CancellationTokenSource _change = new(); // wird abgebrochen, wenn sich die Topics ändern oder neu verbunden werden soll
    private Task? _loop;
    private Timer? _watchdog;
    private DateTimeOffset _lastTick;
    private long _cursor; // höchste gesehene Server-Zeit dieser Sitzung, damit Müll nicht erneut gelesen wird
    private int _failures;
    private int _disposed;

    public RelayReceiver(IRelay relay, ReplayStore replay, RelayReceiverOptions? options = null)
    {
        _relay = relay;
        _replay = replay;
        _options = options ?? new RelayReceiverOptions();
        _lastTick = _options.Clock();
    }

    /// <summary>Der Empfänger ruft die Antwort genau einmal auf, daraus wird die Quittung.</summary>
    public event Action<IncomingLinks, Action<RemoteOutcome>>? LinksReceived;
    /// <summary>Ein Handy hat sich gekoppelt. Der Aufrufer speichert es.</summary>
    public event Action<ReceiverDevice>? Paired;
    /// <summary>Ein Handy hat sich selbst entkoppelt.</summary>
    public event Action<string>? Unpaired;
    public event Action<string>? Seen;
    /// <summary>Auf dem Kanal dieses Handys (Name) kommt viel Ungültiges an. Je Gerät einmal.</summary>
    public event Action<string>? Flooded;
    public event Action? StatusChanged;

    public RelayStatus Status { get; private set; } = RelayStatus.Off;
    /// <summary>Warum neu verbunden wird, sonst null.</summary>
    public string? StatusReason { get; private set; }

    public PairingSession? Pairing
    {
        get { lock (_gate) return _pairing is { } p && p.IsOpen(_options.Clock()) ? p : null; }
    }

    public void Start()
    {
        if (_loop is not null || _disposed != 0) return;
        _watchdog = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _loop = Task.Run(RunAsync);
    }

    public void SetDevices(IEnumerable<ReceiverDevice> devices)
    {
        bool changed;
        lock (_gate)
        {
            var next = devices.ToDictionary(d => d.DeviceId, StringComparer.Ordinal);
            changed = !next.Keys.ToHashSet().SetEquals(_devices.Keys);
            _devices.Clear();
            foreach (var pair in next) _devices[pair.Key] = pair.Value;
        }
        if (changed) Reconnect();
    }

    public PairingSession StartPairing()
    {
        PairingSession session;
        lock (_gate)
        {
            _pairing?.Close();
            _pairing = session = new PairingSession(_options.Clock());
        }
        Reconnect();
        return session;
    }

    public void CancelPairing()
    {
        lock (_gate)
        {
            if (_pairing is null) return;
            _pairing.Close();
            _pairing = null;
        }
        Reconnect();
    }

    /// <summary>Widerruf: Das Topic wird sofort nicht mehr abonniert, das Handy bekommt noch ein <c>bye</c> (ohne Gewähr).</summary>
    public Task RemoveDeviceAsync(string deviceId)
    {
        ReceiverDevice? device;
        lock (_gate)
        {
            if (!_devices.Remove(deviceId, out device)) return Task.CompletedTask;
        }
        _replay.Forget(deviceId);
        Reconnect();
        return SendAsync(device, new RemoteMessage { T = MessageType.Bye });
    }

    /// <summary>Wächter: nach dem Aufwachen (Zeitsprung) neu verbinden, abgelaufene Pairing-Fenster schließen.</summary>
    internal void Tick()
    {
        var now = _options.Clock();
        bool reconnect = false;
        lock (_gate)
        {
            if (now - _lastTick > TimeSpan.FromMinutes(2))
            {
                _failures = 0;
                reconnect = true;
            }
            _lastTick = now;
            if (_pairing is not null && !_pairing.IsOpen(now))
            {
                _pairing = null;
                reconnect = true;
            }
        }
        if (!reconnect) return;
        Reconnect();
        StatusChanged?.Invoke();
    }

    private void Reconnect()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _change;
            _change = new CancellationTokenSource();
        }
        old.Cancel();
    }

    private void SetStatus(RelayStatus status, string? reason = null)
    {
        if (Status == status && StatusReason == reason) return;
        Status = status;
        StatusReason = reason;
        if (_disposed == 0) StatusChanged?.Invoke();
    }

    private (List<string> Topics, long Since) Plan()
    {
        var topics = new List<string>();
        var times = new List<DateTimeOffset>();
        foreach (var device in _devices.Values)
        {
            topics.Add(device.Keys.TopicUp);
            times.Add(device.Paired);
        }
        var now = _options.Clock();
        if (_pairing is { } p && p.IsOpen(now))
        {
            topics.Add(p.Keys.TopicUp);
            times.Add(p.Started);
        }
        if (topics.Count == 0) return (topics, 0);
        topics.Sort(StringComparer.Ordinal);

        // Wiederaufnahme mit 60 s Überlappung, nie vor der ältesten Kopplung und höchstens 13 h zurück.
        long seen = Math.Max(_replay.LastTime ?? 0, _cursor);
        long paired = times.Min().ToUnixTimeSeconds() - 60;
        long oldest = (now - ReplayStore.MaxAge).ToUnixTimeSeconds();
        return (topics, Math.Max(Math.Max(seen - 60, paired), oldest));
    }

    private async Task RunAsync()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            List<string> topics;
            long since;
            CancellationToken change;
            lock (_gate)
            {
                change = _change.Token;
                (topics, since) = Plan();
            }
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop, change);
            try
            {
                if (topics.Count == 0)
                {
                    SetStatus(RelayStatus.Idle);
                    await Task.Delay(Timeout.Infinite, connection.Token).ConfigureAwait(false);
                }
                if (Status != RelayStatus.Connected) SetStatus(RelayStatus.Connecting);
                await ReceiveAsync(topics, since, connection.Token).ConfigureAwait(false);
                connection.Token.ThrowIfCancellationRequested();
                await WaitAsync(RelayStatus.Reconnecting, "Verbindung beendet", null, connection.Token, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested)
            {
                // Beenden oder neue Topic-Liste: ohne Wartezeit weiter.
            }
            catch (RelayException ex) when (ex.Status == 429)
            {
                // Eine neue Topic-Liste darf die Wartezeit nicht abkürzen.
                await WaitAsync(RelayStatus.RateLimited, ex.Message, ex.RetryAfter, stop, stop).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WaitAsync(RelayStatus.Reconnecting, ex.Message, null, connection.Token, stop).ConfigureAwait(false);
            }
        }
        SetStatus(RelayStatus.Off);
    }

    private async Task WaitAsync(RelayStatus status, string reason, TimeSpan? atLeast, CancellationToken wake, CancellationToken stop)
    {
        int step = Math.Min(_failures++, Backoff.Length - 1);
        var wait = TimeSpan.FromSeconds(Backoff[step] * (1 + Random.Shared.NextDouble() * 0.2));
        if (atLeast is { } minimum && minimum > wait)
            wait = minimum < TimeSpan.FromHours(1) ? minimum : TimeSpan.FromHours(1);
        SetStatus(status, reason);
        try
        {
            await _options.Delay(wait, wake).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wake.IsCancellationRequested || stop.IsCancellationRequested)
        {
        }
    }

    private async Task ReceiveAsync(IReadOnlyList<string> topics, long since, CancellationToken ct)
    {
        var started = _options.Clock();
        var minute = started;
        int count = 0;
        await foreach (var e in _relay.SubscribeAsync(topics, since, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            SetStatus(RelayStatus.Connected);
            // Erst eine Verbindung, die gehalten hat, setzt den Backoff zurück.
            if (_options.Clock() - started > TimeSpan.FromMinutes(1)) _failures = 0;
            if (e.Kind != RelayEventKind.Message) continue;

            if (_options.Clock() - minute >= TimeSpan.FromMinutes(1))
            {
                minute = _options.Clock();
                count = 0;
            }
            if (++count > MaxPerMinute)
            {
                await _options.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                minute = _options.Clock();
                count = 1;
            }
            ct.ThrowIfCancellationRequested();
            try
            {
                Handle(e);
            }
            catch (Exception)
            {
                // Ein Fehler bei einer Nachricht beendet nicht den Empfang. Nichts loggen: Es könnte Linktext enthalten.
            }
            _cursor = Math.Max(_cursor, Math.Min(e.Time, _options.Clock().ToUnixTimeSeconds() + 600));
        }
    }

    private void Handle(RelayEvent e)
    {
        ReceiverDevice? device;
        PairingSession? pairing = null;
        lock (_gate)
        {
            device = _devices.Values.FirstOrDefault(d => d.Keys.TopicUp == e.Topic);
            if (device is null && _pairing is { } p && p.Keys.TopicUp == e.Topic && p.IsOpen(_options.Clock())) pairing = p;
        }
        var keys = device?.Keys ?? pairing?.Keys;
        if (keys is null) return;

        byte[]? plain = RemoteCrypto.Open(keys.KeyUp, RemoteCrypto.Up, keys.DeviceId, keys.TopicUp, e.Body);
        var message = plain is null ? null : RemoteMessage.Parse(plain);
        if (message is null)
        {
            CountInvalid(keys.DeviceId, device?.Name);
            return;
        }
        // hello zählt nur im Pairing-Fenster, alles andere nur von gekoppelten Handys.
        if (pairing is not null && message.T != MessageType.Hello) return;
        if (device is not null && message.T is not (MessageType.Links or MessageType.Ping or MessageType.Bye)) return;
        if (!_replay.TryAccept(keys.DeviceId, message.Id!, message.Ts, e.Time)) return;

        if (pairing is not null)
        {
            Pair(pairing, message);
            return;
        }
        Seen?.Invoke(keys.DeviceId);
        switch (message.T)
        {
            case MessageType.Links:
                Links(device!, message);
                break;
            case MessageType.Ping:
                _ = SendAsync(device!, new RemoteMessage
                {
                    T = MessageType.Pong,
                    Ref = message.Id,
                    Pc = _options.PcName,
                    Ver = _options.Version,
                    Mode = _options.Mode(),
                });
                break;
            case MessageType.Bye:
                lock (_gate) _devices.Remove(keys.DeviceId);
                Reconnect();
                Unpaired?.Invoke(keys.DeviceId);
                break;
        }
    }

    private void Pair(PairingSession pairing, RemoteMessage hello)
    {
        var device = new ReceiverDevice(CleanName(hello.Name), pairing.Seed, _options.Clock());
        lock (_gate)
        {
            if (_pairing != pairing) return;
            pairing.Close();
            _pairing = null;
            _devices[device.DeviceId] = device;
        }
        Paired?.Invoke(device);
        bool kept;
        lock (_gate) kept = _devices.ContainsKey(device.DeviceId); // der Aufrufer konnte es nicht speichern und hat es entfernt
        if (kept)
            _ = SendAsync(device, new RemoteMessage { T = MessageType.HelloAck, Pc = _options.PcName, Mode = _options.Mode() });
    }

    internal static string CleanName(string? name)
    {
        var text = new StringBuilder();
        foreach (char c in name ?? "")
        {
            if (!char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) text.Append(c);
            if (text.Length == 40) break;
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        string result = text.ToString().Trim();
        return result.Length > 0 ? result : "Handy";
    }

    private void Links(ReceiverDevice device, RemoteMessage message)
    {
        int answered = 0;
        void Reply(RemoteOutcome outcome)
        {
            if (Interlocked.Exchange(ref answered, 1) != 0) return;
            _ = SendAsync(device, new RemoteMessage
            {
                T = MessageType.Receipt,
                Ref = message.Id,
                State = outcome.State,
                N = outcome.N,
                Skipped = outcome.Skipped > 0 ? outcome.Skipped : null,
                Err = outcome.Err,
            });
        }

        var now = _options.Clock();
        if (!Recent(_linkTimes, device.DeviceId, now, MaxLinkMessages))
        {
            // Höchstens eine Quittung je Minute, sonst beantwortet der PC die Flut.
            if (_limitReplies.TryGetValue(device.DeviceId, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
            _limitReplies[device.DeviceId] = now;
            Reply(new RemoteOutcome(ReceiptState.Rejected, 0, Err: ReceiptError.RateLimited));
            return;
        }
        var links = LinkParser.Extract(message.Text);
        if (links.Count == 0) Reply(new RemoteOutcome(ReceiptState.Rejected, 0, Err: ReceiptError.NoLink));
        else if (links.Count > MaxLinks) Reply(new RemoteOutcome(ReceiptState.Rejected, 0, Err: ReceiptError.TooLarge));
        else LinksReceived?.Invoke(new IncomingLinks(device.DeviceId, device.Name, message.Id!, links), Reply);
    }

    /// <summary>Zählt im 10-Minuten-Fenster. False, wenn die Grenze schon erreicht ist (dann wird nicht gezählt).</summary>
    private static bool Recent(Dictionary<string, Queue<DateTimeOffset>> map, string key, DateTimeOffset now, int limit)
    {
        if (!map.TryGetValue(key, out var times)) map[key] = times = new();
        while (times.Count > 0 && now - times.Peek() >= LimitWindow) times.Dequeue();
        if (times.Count >= limit) return false;
        times.Enqueue(now);
        return true;
    }

    private void CountInvalid(string deviceId, string? name)
    {
        if (name is null) return;
        if (!Recent(_invalid, deviceId, _options.Clock(), MaxInvalid) && _floodReported.Add(deviceId)) Flooded?.Invoke(name);
    }

    /// <summary>Verschlüsselt an das Handy senden, drei Versuche, dann aufgeben (das Handy zeigt dann „abgelaufen“).</summary>
    private async Task SendAsync(ReceiverDevice device, RemoteMessage message)
    {
        message.Id = RemoteCrypto.NewId();
        message.Ts = _options.Clock().ToUnixTimeMilliseconds();
        var keys = device.Keys;
        try
        {
            string body = RemoteCrypto.Seal(keys.KeyDown, RemoteCrypto.Down, keys.DeviceId, keys.TopicDown, message.ToBytes());
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await _relay.PublishAsync(keys.TopicDown, body, _stop.Token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < 3 && !_stop.IsCancellationRequested)
                {
                    var wait = TimeSpan.FromSeconds(attempt == 1 ? 2 : 10);
                    if (ex is RelayException { RetryAfter: { } after } && after > wait && after < TimeSpan.FromMinutes(5)) wait = after;
                    await _options.Delay(wait, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // keine Quittung ist kein Fehler des Programms
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watchdog?.Dispose();
        _stop.Cancel();
        // Den Client erst freigeben, wenn die Schleife zu Ende ist.
        if (_loop is { } loop) loop.ContinueWith(_ => _relay.Dispose(), TaskScheduler.Default);
        else _relay.Dispose();
        Status = RelayStatus.Off;
    }
}
