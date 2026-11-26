using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Remote;

public enum RelayEventKind { Open, Keepalive, Message }

/// <param name="Time">Unix-Zeit des Servers in Sekunden.</param>
public sealed record RelayEvent(RelayEventKind Kind, string? Id = null, long Time = 0, string? Topic = null, string? Body = null);

/// <summary>Der Server hat abgelehnt. 429 mit <see cref="RetryAfter"/>, wenn er eine Wartezeit nennt.</summary>
public sealed class RelayException : Exception
{
    public RelayException(int status, TimeSpan? retryAfter = null) : base("HTTP " + status)
    {
        Status = status;
        RetryAfter = retryAfter;
    }

    public int Status { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Der Vermittler: ntfy in Wirklichkeit, eine Attrappe in den Tests. Er sieht nur Chiffrat.</summary>
public interface IRelay : IDisposable
{
    Task PublishAsync(string topic, string body, CancellationToken ct);

    /// <summary>Dauerverbindung über alle Topics. Endet nur durch Abbruch, Fehler oder Verbindungsende.</summary>
    IAsyncEnumerable<RelayEvent> SubscribeAsync(IReadOnlyList<string> topics, long since, CancellationToken ct);

    /// <summary>Einmal den Zwischenspeicher abholen.</summary>
    Task<IReadOnlyList<RelayEvent>> PollAsync(string topic, long since, CancellationToken ct);
}
