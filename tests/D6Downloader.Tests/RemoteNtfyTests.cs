using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Remote;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>NtfyRelay gegen konservierte Zeilen und eine kleine Attrappe auf 127.0.0.1. Nie gegen einen echten Server.</summary>
public class RemoteNtfyTests
{
    private const string Open = """{"id":"a1","time":1700000000,"event":"open","topic":"t1"}""";
    private const string Keepalive = """{"id":"a2","time":1700000045,"event":"keepalive","topic":"t1"}""";
    private const string Message = """{"id":"a3","time":1700000050,"expires":1700043250,"event":"message","topic":"t1","message":"d6r1.abc"}""";

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = null!;
        public Uri Url { get; } = null!;
        public List<(string Method, string Path, string Body, string? Firebase, string? Agent)> Requests { get; } = new();
        public Action<HttpListenerContext> Respond { get; set; } = c => c.Response.Close();

        public Server()
        {
            for (int attempt = 1; ; attempt++)
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                Url = new Uri($"http://127.0.0.1:{port}/");
                try { _listener.Start(); break; }
                catch (HttpListenerException) when (attempt < 10) { }
            }
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }
                    string body = new StreamReader(context.Request.InputStream).ReadToEnd();
                    lock (Requests)
                        Requests.Add((context.Request.HttpMethod, context.Request.Url!.PathAndQuery, body,
                            context.Request.Headers["X-Firebase"], context.Request.Headers["User-Agent"]));
                    try { Respond(context); }
                    catch (Exception) { }
                }
            });
        }

        public static void Lines(HttpListenerContext c, params string[] lines)
        {
            byte[] data = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            c.Response.SendChunked = true;
            c.Response.OutputStream.Write(data);
            c.Response.OutputStream.Flush();
        }

        public void Dispose()
        {
            try { _listener.Close(); } catch { }
        }
    }

    private static NtfyRelay Relay(Server server) => NtfyRelay.ForLoopbackTest(server.Url, "d6downloader/test");

    [Fact]
    public void Lines_are_parsed_and_unknown_ones_ignored()
    {
        Assert.Equal(RelayEventKind.Open, NtfyRelay.ParseLine(Open)!.Kind);
        Assert.Equal(RelayEventKind.Keepalive, NtfyRelay.ParseLine(Keepalive)!.Kind);
        Assert.Equal(new RelayEvent(RelayEventKind.Message, "a3", 1700000050, "t1", "d6r1.abc"), NtfyRelay.ParseLine(Message));
        foreach (string line in new[] { "", "nix", "[]", "{}", """{"event":"poll_request"}""", """{"event":"message","topic":"t"}""",
                     """{"event":5}""" })
            Assert.Null(NtfyRelay.ParseLine(line));
    }

    [Fact]
    public void Plain_http_is_refused_outside_tests()
    {
        Assert.Throws<ArgumentException>(() => new NtfyRelay(new Uri("http://example.com/"), "x"));
        Assert.Throws<ArgumentException>(() => NtfyRelay.ForLoopbackTest(new Uri("http://example.com/"), "x"));
    }

    [Fact]
    public async Task Publish_posts_the_body_without_firebase()
    {
        using var server = new Server();
        using var relay = Relay(server);

        await relay.PublishAsync("d6rabc", "d6r1.xyz", default);

        var request = Assert.Single(server.Requests);
        Assert.Equal(("POST", "/d6rabc", "d6r1.xyz", "no", "d6downloader/test"), request);
    }

    [Fact]
    public async Task Rate_limit_carries_retry_after_and_redirects_are_not_followed()
    {
        using var server = new Server();
        using var relay = Relay(server);
        server.Respond = c =>
        {
            c.Response.StatusCode = 429;
            c.Response.AddHeader("Retry-After", "17");
            c.Response.Close();
        };
        var limited = await Assert.ThrowsAsync<RelayException>(() => relay.PublishAsync("t", "x", default));
        Assert.Equal((429, TimeSpan.FromSeconds(17)), (limited.Status, limited.RetryAfter));

        server.Respond = c =>
        {
            c.Response.Redirect("http://127.0.0.1:1/");
            c.Response.Close();
        };
        var moved = await Assert.ThrowsAsync<RelayException>(() => relay.PublishAsync("t", "x", default));
        Assert.Equal(302, moved.Status);
    }

    [Fact]
    public async Task The_stream_yields_events_and_ends_when_the_server_breaks_off()
    {
        using var server = new Server();
        using var relay = Relay(server);
        server.Respond = c =>
        {
            Server.Lines(c, Open, Keepalive, "müll", new string('x', NtfyRelay.MaxLine + 10), Message);
            c.Response.OutputStream.Write(Encoding.UTF8.GetBytes("""{"event":"mess"""));
            c.Response.OutputStream.Flush();
            c.Response.Abort();
        };
        var kinds = new List<RelayEventKind>();
        try
        {
            await foreach (var e in relay.SubscribeAsync(new[] { "t1", "t2" }, 123, default)) kinds.Add(e.Kind);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException)
        {
        }

        Assert.Equal(new[] { RelayEventKind.Open, RelayEventKind.Keepalive, RelayEventKind.Message }, kinds);
        Assert.Equal("/t1,t2/json?since=123", server.Requests[0].Path);
    }

    [Fact]
    public async Task A_silent_stream_times_out()
    {
        using var server = new Server();
        using var relay = Relay(server);
        relay.IdleTimeout = TimeSpan.FromMilliseconds(300);
        server.Respond = c => Server.Lines(c, Open); // bleibt offen und schweigt

        var kinds = new List<RelayEventKind>();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var e in relay.SubscribeAsync(new[] { "t1" }, 0, default)) kinds.Add(e.Kind);
        });
        Assert.Equal(new[] { RelayEventKind.Open }, kinds);
    }

    [Fact]
    public async Task Poll_returns_only_messages()
    {
        using var server = new Server();
        using var relay = Relay(server);
        server.Respond = c =>
        {
            Server.Lines(c, Open, Message);
            c.Response.Close();
        };

        var messages = await relay.PollAsync("t1", 5, default);

        Assert.Equal("d6r1.abc", Assert.Single(messages).Body);
        Assert.Equal("/t1/json?poll=1&since=5", server.Requests[0].Path);
    }
}
