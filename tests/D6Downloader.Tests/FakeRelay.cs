using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using D6Downloader.Remote;

namespace D6Downloader.Tests;

/// <summary>Vermittler im Speicher: Zwischenspeicher, Dauerverbindung, Fehler auf Bestellung. Kein Netz.</summary>
internal sealed class FakeRelay : IRelay
{
    private readonly object _gate = new();
    private readonly List<RelayEvent> _cache = new();
    private readonly List<(HashSet<string> Topics, Channel<RelayEvent> Channel)> _open = new();
    private int _ids;

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public List<(string Topic, string Body)> Published { get; } = new();
    public List<(string[] Topics, long Since)> Subscriptions { get; } = new();
    /// <summary>Die nächsten Verbindungsversuche scheitern damit.</summary>
    public Queue<Exception> SubscribeFailures { get; } = new();
    public int PublishFailures;
    public bool Disposed { get; private set; }

    public int OpenStreams { get { lock (_gate) return _open.Count; } }

    public string[] CurrentTopics
    {
        get { lock (_gate) return _open.Count == 0 ? Array.Empty<string>() : _open[^1].Topics.ToArray(); }
    }

    /// <summary>Eine Nachricht, wie sie ein Handy (oder ein Fremder) schickt.</summary>
    public void Inject(string topic, string body)
    {
        lock (_gate)
        {
            var e = new RelayEvent(RelayEventKind.Message, "m" + ++_ids, Clock().ToUnixTimeSeconds(), topic, body);
            _cache.Add(e);
            foreach (var (topics, channel) in _open)
                if (topics.Contains(topic)) channel.Writer.TryWrite(e);
        }
    }

    /// <summary>Alle Verbindungen enden, wie bei einem Netzwechsel.</summary>
    public void Drop()
    {
        lock (_gate)
            foreach (var (_, channel) in _open) channel.Writer.TryComplete();
    }

    public Task PublishAsync(string topic, string body, CancellationToken ct)
    {
        if (Interlocked.Decrement(ref PublishFailures) >= 0) throw new RelayException(500);
        lock (Published) Published.Add((topic, body));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RelayEvent> SubscribeAsync(IReadOnlyList<string> topics, long since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<RelayEvent>();
        var entry = (topics.ToHashSet(), channel);
        lock (_gate)
        {
            Subscriptions.Add((topics.ToArray(), since));
            if (SubscribeFailures.Count > 0) throw SubscribeFailures.Dequeue();
            channel.Writer.TryWrite(new RelayEvent(RelayEventKind.Open));
            foreach (var e in _cache)
                if (entry.Item1.Contains(e.Topic!) && e.Time >= since) channel.Writer.TryWrite(e);
            _open.Add(entry);
        }
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
                while (channel.Reader.TryRead(out var e)) yield return e;
        }
        finally
        {
            lock (_gate) _open.Remove(entry);
        }
    }

    public Task<IReadOnlyList<RelayEvent>> PollAsync(string topic, long since, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<RelayEvent>>(_cache.Where(e => e.Topic == topic && e.Time >= since).ToList());
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Das Handy in den Tests: verschlüsselt wie die App und liest die Antworten des PCs.</summary>
internal sealed class FakePhone
{
    public FakePhone(byte[] seed) => Keys = RemoteCrypto.Derive(seed);

    public RemoteKeys Keys { get; }
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public string Seal(RemoteMessage message)
    {
        message.Id ??= RemoteCrypto.NewId();
        if (message.Ts == 0) message.Ts = Clock().ToUnixTimeMilliseconds();
        return RemoteCrypto.Seal(Keys.KeyUp, RemoteCrypto.Up, Keys.DeviceId, Keys.TopicUp, message.ToBytes());
    }

    public RemoteMessage Send(FakeRelay relay, RemoteMessage message)
    {
        relay.Inject(Keys.TopicUp, Seal(message));
        return message;
    }

    public RemoteMessage Links(FakeRelay relay, params string[] links) =>
        Send(relay, new RemoteMessage { T = MessageType.Links, Text = string.Join("\n", links), Src = "share" });

    public List<RemoteMessage> Replies(FakeRelay relay)
    {
        (string Topic, string Body)[] published;
        lock (relay.Published) published = relay.Published.ToArray();
        return published.Where(p => p.Topic == Keys.TopicDown)
            .Select(p => RemoteMessage.Parse(RemoteCrypto.Open(Keys.KeyDown, RemoteCrypto.Down, Keys.DeviceId, Keys.TopicDown, p.Body)!)!)
            .ToList();
    }
}
