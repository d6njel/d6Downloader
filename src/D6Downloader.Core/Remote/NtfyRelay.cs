using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Remote;

/// <summary>
/// ntfy über HTTPS, ohne Konto. Weiterleitungen werden nicht verfolgt, Zeilen sind in der Länge begrenzt (der Server gilt als
/// nicht vertrauenswürdig).
/// </summary>
public sealed class NtfyRelay : IRelay
{
    internal const int MaxLine = 32 * 1024;

    private readonly HttpClient _client;
    private readonly Uri _server;

    public NtfyRelay(Uri server, string userAgent)
        : this(RemoteSettings.ParseServer(server.AbsoluteUri)
            ?? throw new ArgumentException("Nur https://-Adressen.", nameof(server)), userAgent, checkedServer: true)
    {
    }

    private NtfyRelay(Uri server, string userAgent, bool checkedServer)
    {
        _server = server;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    /// <summary>Nur für Tests: eine Attrappe auf dem eigenen Rechner ohne TLS.</summary>
    internal static NtfyRelay ForLoopbackTest(Uri server, string userAgent) => server.IsLoopback
        ? new NtfyRelay(server, userAgent, checkedServer: false)
        : throw new ArgumentException("Nur Loopback.", nameof(server));

    /// <summary>So lange darf der Strom schweigen (ntfy schickt etwa alle 45 s ein Lebenszeichen).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public async Task PublishAsync(string topic, string body, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, topic))
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("X-Firebase", "no");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            Check(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    public async IAsyncEnumerable<RelayEvent> SubscribeAsync(IReadOnlyList<string> topics, long since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var uri = new Uri(_server, $"{string.Join(",", topics)}/json?since={since}");
        await foreach (var e in ReadAsync(uri, ct).ConfigureAwait(false)) yield return e;
    }

    public async Task<IReadOnlyList<RelayEvent>> PollAsync(string topic, long since, CancellationToken ct)
    {
        var result = new List<RelayEvent>();
        await foreach (var e in ReadAsync(new Uri(_server, $"{topic}/json?poll=1&since={since}"), ct).ConfigureAwait(false))
        {
            if (e.Kind == RelayEventKind.Message) result.Add(e);
            if (result.Count >= 1000) break;
        }
        return result;
    }

    private async IAsyncEnumerable<RelayEvent> ReadAsync(Uri uri, [EnumeratorCancellation] CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage response;
        Stream stream;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
        using (response)
        {
            Check(response);
            stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var chunk = new byte[8192];
            var line = new MemoryStream();
            bool tooLong = false;
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                int read;
                try
                {
                    read = await stream.ReadAsync(chunk, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException();
                }
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] == (byte)'\n')
                    {
                        var parsed = tooLong ? null : ParseLine(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                        line.SetLength(0);
                        tooLong = false;
                        if (parsed is not null) yield return parsed;
                    }
                    else if (!tooLong)
                    {
                        line.WriteByte(chunk[i]);
                        tooLong = line.Length > MaxLine;
                    }
                }
            }
            if (!tooLong && ParseLine(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length)) is { } last)
                yield return last;
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;
        TimeSpan? wait = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        throw new RelayException((int)response.StatusCode, wait);
    }

    /// <summary>Eine JSON-Zeile des Stroms. Unbekannte Ereignisse und Unlesbares ergeben null.</summary>
    internal static RelayEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            long time = root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out long v)
                ? v : 0;
            return Text("event") switch
            {
                "open" => new RelayEvent(RelayEventKind.Open, Text("id"), time),
                "keepalive" => new RelayEvent(RelayEventKind.Keepalive, Text("id"), time),
                "message" when Text("topic") is { } topic && Text("message") is { } body =>
                    new RelayEvent(RelayEventKind.Message, Text("id"), time, topic, body),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
