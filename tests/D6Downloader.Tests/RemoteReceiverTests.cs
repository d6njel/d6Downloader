using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Remote;
using Xunit;

namespace D6Downloader.Tests;

public class RemoteReceiverTests
{
    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly FakeRelay Relay = new();
        public readonly byte[] Seed = RandomNumberGenerator.GetBytes(16);
        public readonly FakePhone Phone;
        public readonly List<TimeSpan> Waits = new();
        public readonly List<IncomingLinks> Links = new();
        public readonly List<string> Floods = new(), Unpaired = new();
        public readonly List<ReceiverDevice> Paired = new();
        public RemoteOutcome? Answer = new(ReceiptState.PendingConfirmation, 1);
        public RelayReceiver Receiver = null!;

        public Rig(bool paired = true)
        {
            Relay.Clock = () => Now;
            Phone = new FakePhone(Seed) { Clock = () => Now };
            Create();
            if (paired) Receiver.SetDevices(new[] { Device() });
        }

        public ReceiverDevice Device() => new("Pixel", Seed, Now);

        public void Create()
        {
            Receiver = new RelayReceiver(Relay, new ReplayStore(() => _dir.File("remote-state.json"), () => Now), new RelayReceiverOptions
            {
                PcName = "test-pc",
                Version = "9.9",
                Clock = () => Now,
                Delay = async (wait, ct) =>
                {
                    lock (Waits) Waits.Add(wait);
                    await Task.Delay(5, ct);
                },
            });
            Receiver.LinksReceived += (links, reply) =>
            {
                lock (Links) Links.Add(links);
                if (Answer is { } answer) reply(answer);
            };
            Receiver.Flooded += Floods.Add;
            Receiver.Unpaired += Unpaired.Add;
            Receiver.Paired += Paired.Add;
        }

        public async Task Start()
        {
            Receiver.Start();
            await Until(() => Relay.OpenStreams == 1);
        }

        /// <summary>Wartet, bis der Empfänger alles Vorherige verarbeitet hat (ein Ping wird beantwortet).</summary>
        public async Task Sync()
        {
            var ping = Phone.Send(Relay, new RemoteMessage { T = MessageType.Ping });
            await Until(() => Phone.Replies(Relay).Any(r => r.T == MessageType.Pong && r.Ref == ping.Id));
        }

        public static async Task Until(Func<bool> done)
        {
            var end = DateTime.UtcNow.AddSeconds(15);
            while (!done())
            {
                Assert.True(DateTime.UtcNow < end, "Zeitüberschreitung");
                await Task.Delay(5);
            }
        }

        public void Dispose()
        {
            Receiver.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public async Task Links_raise_the_event_and_send_a_receipt()
    {
        using var rig = new Rig();
        await rig.Start();

        var sent = rig.Phone.Links(rig.Relay, "https://example.com/a.zip", "https://example.com/b.zip");
        await Rig.Until(() => rig.Phone.Replies(rig.Relay).Count == 1);

        var links = Assert.Single(rig.Links);
        Assert.Equal(new[] { "https://example.com/a.zip", "https://example.com/b.zip" }, links.Links);
        Assert.Equal("Pixel", links.DeviceName);
        var receipt = Assert.Single(rig.Phone.Replies(rig.Relay));
        Assert.Equal((MessageType.Receipt, sent.Id, ReceiptState.PendingConfirmation, 1),
            (receipt.T, receipt.Ref, receipt.State, receipt.N));
        Assert.Equal(RelayStatus.Connected, rig.Receiver.Status);
    }

    [Fact]
    public async Task The_same_message_is_processed_once_also_after_a_restart()
    {
        using var rig = new Rig();
        await rig.Start();
        string body = rig.Phone.Seal(new RemoteMessage { T = MessageType.Links, Text = "https://example.com/a.zip" });
        rig.Relay.Inject(rig.Phone.Keys.TopicUp, body);
        rig.Relay.Inject(rig.Phone.Keys.TopicUp, body);
        await rig.Sync();
        Assert.Single(rig.Links);

        rig.Receiver.Dispose();
        await Rig.Until(() => rig.Relay.OpenStreams == 0);
        rig.Create();
        rig.Receiver.SetDevices(new[] { rig.Device() });
        await rig.Start();
        rig.Relay.Inject(rig.Phone.Keys.TopicUp, body);
        await rig.Sync();
        Assert.Single(rig.Links);
    }

    [Fact]
    public async Task Too_old_and_future_messages_are_ignored()
    {
        using var rig = new Rig();
        await rig.Start();
        foreach (var offset in new[] { TimeSpan.FromHours(-14), TimeSpan.FromMinutes(11) })
            rig.Phone.Send(rig.Relay, new RemoteMessage
            {
                T = MessageType.Links,
                Text = "https://example.com/a.zip",
                Ts = (rig.Now + offset).ToUnixTimeMilliseconds(),
            });
        await rig.Sync();
        Assert.Empty(rig.Links);
    }

    [Fact]
    public async Task Without_link_or_with_too_many_links_the_message_is_rejected()
    {
        using var rig = new Rig();
        await rig.Start();
        rig.Phone.Links(rig.Relay, "ftp://example.com/a", "nur text");
        rig.Phone.Links(rig.Relay, Enumerable.Range(0, 51).Select(i => $"http://e.de/{i}").ToArray());
        await Rig.Until(() => rig.Phone.Replies(rig.Relay).Count == 2);
        Assert.Equal(new[] { ReceiptError.NoLink, ReceiptError.TooLarge },
            rig.Phone.Replies(rig.Relay).Select(r => r.Err).Order());
        Assert.Empty(rig.Links);
    }

    [Fact]
    public async Task Ping_is_answered_and_bye_removes_the_phone()
    {
        using var rig = new Rig();
        await rig.Start();
        await rig.Sync();
        var pong = rig.Phone.Replies(rig.Relay)[0];
        Assert.Equal(("test-pc", "9.9", ReceiveMode.Dialog), (pong.Pc, pong.Ver, pong.Mode));

        rig.Phone.Send(rig.Relay, new RemoteMessage { T = MessageType.Bye });
        await Rig.Until(() => rig.Receiver.Status == RelayStatus.Idle);
        Assert.Equal(rig.Phone.Keys.DeviceId, Assert.Single(rig.Unpaired));
        Assert.Equal(0, rig.Relay.OpenStreams);
    }

    [Fact]
    public async Task Revoking_stops_the_subscription_and_says_bye()
    {
        using var rig = new Rig();
        var other = new ReceiverDevice("Anderes", RandomNumberGenerator.GetBytes(16), rig.Now);
        rig.Receiver.SetDevices(new[] { rig.Device(), other });
        await rig.Start();

        await rig.Receiver.RemoveDeviceAsync(rig.Phone.Keys.DeviceId);
        await Rig.Until(() => rig.Relay.CurrentTopics.SequenceEqual(new[] { other.Keys.TopicUp }));
        rig.Phone.Links(rig.Relay, "https://example.com/a.zip");
        await Task.Delay(100);

        Assert.Empty(rig.Links);
        Assert.Equal(MessageType.Bye, Assert.Single(rig.Phone.Replies(rig.Relay)).T);
    }

    [Fact]
    public async Task Hello_pairs_only_within_the_window()
    {
        using var rig = new Rig(paired: false);
        rig.Receiver.Start();
        await Rig.Until(() => rig.Receiver.Status == RelayStatus.Idle);
        Assert.Empty(rig.Relay.Subscriptions); // ohne Kopplung keine Verbindung

        var session = rig.Receiver.StartPairing();
        var phone = new FakePhone(session.Seed) { Clock = () => rig.Now };
        await Rig.Until(() => rig.Relay.OpenStreams == 1);
        phone.Links(rig.Relay, "https://example.com/zu-frueh.zip"); // vor hello: nichts
        phone.Send(rig.Relay, new RemoteMessage { T = MessageType.Hello, Name = new string('N', 60) + "\n" });
        await Rig.Until(() => phone.Replies(rig.Relay).Count == 1);

        var device = Assert.Single(rig.Paired);
        Assert.Equal(40, device.Name.Length);
        var ack = phone.Replies(rig.Relay)[0];
        Assert.Equal((MessageType.HelloAck, "test-pc", ReceiveMode.Dialog), (ack.T, ack.Pc, ack.Mode));
        Assert.Null(rig.Receiver.Pairing);
        Assert.Empty(rig.Links);

        phone.Links(rig.Relay, "https://example.com/a.zip");
        await Rig.Until(() => rig.Links.Count == 1);
    }

    [Fact]
    public async Task An_expired_or_cancelled_window_pairs_nobody()
    {
        using var rig = new Rig(paired: false);
        rig.Receiver.Start();
        var session = rig.Receiver.StartPairing();
        var phone = new FakePhone(session.Seed) { Clock = () => rig.Now };
        await Rig.Until(() => rig.Relay.OpenStreams == 1);

        rig.Now += TimeSpan.FromMinutes(11);
        phone.Send(rig.Relay, new RemoteMessage { T = MessageType.Hello, Name = "spät" });
        await Task.Delay(100);
        Assert.Empty(rig.Paired);
        Assert.Null(rig.Receiver.Pairing);
        rig.Receiver.Tick();
        await Rig.Until(() => rig.Receiver.Status == RelayStatus.Idle);

        session = rig.Receiver.StartPairing();
        phone = new FakePhone(session.Seed) { Clock = () => rig.Now };
        rig.Receiver.CancelPairing();
        phone.Send(rig.Relay, new RemoteMessage { T = MessageType.Hello, Name = "abgebrochen" });
        await Task.Delay(100);
        Assert.Empty(rig.Paired);
        Assert.Empty(rig.Relay.Published);
    }

    [Fact]
    public async Task A_flood_of_foreign_messages_raises_one_notice_and_is_throttled()
    {
        using var rig = new Rig();
        await rig.Start();
        for (int i = 0; i < 1000; i++) rig.Relay.Inject(rig.Phone.Keys.TopicUp, "d6r1." + Convert.ToBase64String(RandomNumberGenerator.GetBytes(60)));
        await rig.Sync();

        Assert.Empty(rig.Links);
        Assert.Equal("Pixel", Assert.Single(rig.Floods));
        Assert.Equal(5, rig.Waits.Count(w => w == TimeSpan.FromSeconds(60))); // 1001 Nachrichten, je 200 eine Pause
        Assert.Single(rig.Relay.Published); // nur die Antwort auf den Ping
    }

    [Fact]
    public async Task More_than_thirty_link_messages_are_rate_limited_with_one_receipt_per_minute()
    {
        using var rig = new Rig();
        await rig.Start();
        for (int i = 0; i < 35; i++) rig.Phone.Links(rig.Relay, $"https://example.com/{i}.zip");
        await rig.Sync();
        await Rig.Until(() => rig.Phone.Replies(rig.Relay).Count == 32);

        Assert.Equal(30, rig.Links.Count);
        Assert.Single(rig.Phone.Replies(rig.Relay), r => r.Err == ReceiptError.RateLimited);

        rig.Now += TimeSpan.FromMinutes(10);
        rig.Phone.Links(rig.Relay, "https://example.com/wieder.zip");
        await Rig.Until(() => rig.Links.Count == 31);
    }

    [Fact]
    public async Task Errors_back_off_and_reconnect_from_the_last_message()
    {
        using var rig = new Rig();
        rig.Relay.SubscribeFailures.Enqueue(new TimeoutException());
        rig.Relay.SubscribeFailures.Enqueue(new RelayException(429, TimeSpan.FromSeconds(90)));
        rig.Relay.SubscribeFailures.Enqueue(new RelayException(500));
        await rig.Start();

        Assert.Equal(3, rig.Waits.Count);
        Assert.InRange(rig.Waits[0].TotalSeconds, 2, 2.5);
        Assert.Equal(90, rig.Waits[1].TotalSeconds);
        Assert.InRange(rig.Waits[2].TotalSeconds, 10, 12.1);
        // Ohne verarbeitete Nachricht: ab der Kopplung (mit 60 s Spielraum).
        Assert.Equal(rig.Device().Paired.ToUnixTimeSeconds() - 60, rig.Relay.Subscriptions[^1].Since);

        rig.Now += TimeSpan.FromHours(1);
        await rig.Sync();
        rig.Relay.Drop();
        await Rig.Until(() => rig.Relay.Subscriptions.Count == 5 && rig.Relay.OpenStreams == 1);
        Assert.Equal(rig.Now.ToUnixTimeSeconds() - 60, rig.Relay.Subscriptions[^1].Since);
    }

    [Fact]
    public async Task Since_never_goes_back_more_than_thirteen_hours()
    {
        using var rig = new Rig(paired: false);
        rig.Receiver.SetDevices(new[] { new ReceiverDevice("alt", rig.Seed, rig.Now.AddDays(-30)) });
        await rig.Start();
        Assert.Equal((rig.Now - TimeSpan.FromHours(13)).ToUnixTimeSeconds(), rig.Relay.Subscriptions[0].Since);
    }

    [Fact]
    public async Task A_new_device_list_or_a_wake_up_reconnects()
    {
        using var rig = new Rig();
        await rig.Start();
        var other = new ReceiverDevice("Anderes", RandomNumberGenerator.GetBytes(16), rig.Now);

        rig.Receiver.SetDevices(new[] { rig.Device(), other });
        await Rig.Until(() => rig.Relay.CurrentTopics.Length == 2);
        int connections = rig.Relay.Subscriptions.Count;

        rig.Receiver.SetDevices(new[] { rig.Device(), other }); // unverändert: keine neue Verbindung
        rig.Receiver.Tick();
        await Task.Delay(50);
        Assert.Equal(connections, rig.Relay.Subscriptions.Count);

        rig.Now += TimeSpan.FromMinutes(5); // Standby
        rig.Receiver.Tick();
        await Rig.Until(() => rig.Relay.Subscriptions.Count == connections + 1);
        Assert.Empty(rig.Waits);
    }

    [Fact]
    public async Task A_failing_receipt_is_retried_three_times_at_most()
    {
        using var rig = new Rig();
        await rig.Start();
        rig.Relay.PublishFailures = 2;
        rig.Phone.Links(rig.Relay, "https://example.com/a.zip");
        await Rig.Until(() => rig.Relay.Published.Count == 1);
        Assert.Equal(2, rig.Waits.Count);
    }
}
