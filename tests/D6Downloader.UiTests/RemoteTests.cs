using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using D6Downloader.Downloads;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Remote;
using D6Downloader.Sheets;
using D6Downloader.Tests;
using Xunit;

namespace D6Downloader.UiTests;

public class RemoteTests
{
    /// <summary>Nur der lokale Testserver ist trotz Zielschutz erlaubt; DNS wird nie gefragt.</summary>
    private static void AllowTestServer(Ui ui, TestServer server)
    {
        int port = new Uri(server.Url).Port;
        ui.Window.RemoteTargets = new TargetGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }))
        {
            Exempt = uri => uri.Host == "127.0.0.1" && uri.Port == port,
        };
    }

    private static List<RemoteOutcome> Receive(Ui ui, bool auto, params string[] links)
    {
        var outcomes = new List<RemoteOutcome>();
        ui.Window.ReceiveRemoteLinks(string.Join("\n", links), auto, outcomes.Add);
        Ui.Pump();
        return outcomes;
    }

    private static async Task<RemoteOutcome> Outcome(List<RemoteOutcome> outcomes)
    {
        await Ui.WaitUntil(() => outcomes.Count > 0, "Quittung");
        return Assert.Single(outcomes);
    }

    [AvaloniaFact]
    public void Dialog_mode_opens_the_add_sheet_and_starts_nothing()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000);

        var outcomes = Receive(ui, auto: false, server.Url);

        Assert.Equal(new RemoteOutcome(ReceiptState.PendingConfirmation, 1), Assert.Single(outcomes));
        Assert.False(ui.Sheet<AddSheet>().IsAuto);
        Assert.Empty(ui.Downloads.Items);
    }

    [AvaloniaTheory]
    [InlineData("shown")]
    [InlineData("hidden")]
    [InlineData("never-shown")]
    public async Task Auto_mode_starts_the_downloads_even_with_the_window_hidden(string window)
    {
        using var ui = new Ui(show: window != "never-shown");
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);
        if (window == "hidden") ui.Window.Hide();

        var outcomes = Receive(ui, auto: true, server.Url + "?a", server.Url + "?b");
        var outcome = await Outcome(outcomes);

        Assert.Equal(new RemoteOutcome(ReceiptState.Queued, 2), outcome);
        Assert.Equal(2, ui.Downloads.Items.Count);
        Assert.All(ui.Downloads.Items, i => Assert.NotEqual(DownloadStatus.Paused, i.Status));
        Assert.All(ui.Downloads.Items, i => Assert.Equal(ui.Dir.Path, i.Folder));
        Assert.False(ui.Window.IsSheetOpen);
        Assert.Equal(window == "shown", ui.Window.IsVisible);
        Assert.False(ui.Downloads.Settings.StartImmediately); // die Einstellung des Dialogs bleibt
        Assert.Empty(ui.Downloads.Settings.RecentFolders);
        await Ui.WaitUntil(() => ui.Downloads.Items.All(i => i.Status == DownloadStatus.Completed), "Download");
    }

    [AvaloniaFact]
    public async Task Duplicates_and_existing_files_are_skipped()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);
        ui.Downloads.Add(new[] { new DownloadItem(server.Url + "?alt", "alt.bin", ui.Dir.Path) }, start: false);

        var first = await Outcome(Receive(ui, auto: true, server.Url + "?alt", server.Url + "?neu"));
        Assert.Equal(new RemoteOutcome(ReceiptState.Queued, 1, 1), first);

        var second = await Outcome(Receive(ui, auto: true, server.Url + "?neu"));
        Assert.Equal(new RemoteOutcome(ReceiptState.Rejected, 0, 1, ReceiptError.AllDuplicate), second);

        File.WriteAllText(Path.Combine(ui.Dir.Path, "file.bin"), "schon da");
        var third = await Outcome(Receive(ui, auto: true, server.Url + "?drittes"));
        Assert.Equal(new RemoteOutcome(ReceiptState.Rejected, 0, 1, ReceiptError.AllDuplicate), third);
        Assert.Equal(2, ui.Downloads.Items.Count);
        Assert.False(ui.Window.IsSheetOpen);
    }

    [AvaloniaFact]
    public async Task Deselect_rules_apply_like_in_the_dialog()
    {
        using var ui = new Ui(s => s.AutoDeselect = new() { ".bin" });
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);

        var outcome = await Outcome(Receive(ui, auto: true, server.Url));

        Assert.Equal(new RemoteOutcome(ReceiptState.Rejected, 0, 1, ReceiptError.Filtered), outcome);
        Assert.Empty(ui.Downloads.Items);
    }

    [AvaloniaFact]
    public async Task Local_targets_are_rejected_without_any_request()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000);

        var outcome = await Outcome(Receive(ui, auto: true, server.Url, "http://192.168.1.1/x", "http://router/y"));

        Assert.Equal(new RemoteOutcome(ReceiptState.Rejected, 0, 3, ReceiptError.BlockedTarget), outcome);
        Assert.Equal(0, server.RequestCount);
        Assert.Empty(ui.Downloads.Items);
        Assert.False(ui.Window.IsSheetOpen);
    }

    [AvaloniaFact]
    public async Task A_failing_link_stays_in_the_dialog_and_the_others_start()
    {
        using var ui = new Ui(show: false);
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);
        string broken = "http://127.0.0.1:1/weg.bin";
        ui.Window.RemoteTargets.Exempt = uri => uri.Host == "127.0.0.1";

        var outcome = await Outcome(Receive(ui, auto: true, server.Url, broken));

        Assert.Equal(new RemoteOutcome(ReceiptState.Partial, 1), outcome);
        Assert.Single(ui.Downloads.Items);
        var sheet = ui.Sheet<AddSheet>();
        Assert.False(sheet.IsAuto);
        Assert.Equal(broken, Assert.Single(sheet.Rows.OfType<AddCandidate>()).Url);
        Assert.True(ui.Window.IsVisible); // Fenster nach vorn
    }

    [AvaloniaFact]
    public async Task A_probe_that_takes_too_long_goes_to_the_dialog()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000) { StallSegmentRequests = 100, ThrottleBytesPerSecond = 1 };
        AllowTestServer(ui, server);
        ui.Window.RemoteTimeout = TimeSpan.Zero;

        var outcome = await Outcome(Receive(ui, auto: true, server.Url));

        Assert.Equal(new RemoteOutcome(ReceiptState.PendingConfirmation, 1), outcome);
        Assert.Empty(ui.Downloads.Items);
        Assert.True(ui.Window.IsSheetOpen);
    }

    [AvaloniaFact]
    public async Task Rapidgator_waiting_for_a_code_goes_to_the_dialog()
    {
        using var ui = new Ui();
        ui.Rapidgator.TwoFactor = true;
        try
        {
            await ui.Downloads.Rapidgator.LoginAsync(FakeRapidgator.Login, FakeRapidgator.Password, null, default);
        }
        catch (Debrid.DebridException)
        {
        }
        Ui.Pump();
        Assert.Equal(RapidgatorState.CodeRequired, ui.Downloads.Rapidgator.State);
        ui.Window.RemoteTargets = new TargetGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var outcome = await Outcome(Receive(ui, auto: true, FakeRapidgator.FileUrl));

        Assert.Equal(ReceiptState.PendingConfirmation, outcome.State);
        Assert.Empty(ui.Downloads.Items);
        Assert.True(ui.Window.IsSheetOpen);
    }

    [AvaloniaFact]
    public void With_another_sheet_open_nothing_starts_by_itself()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);
        SettingsSheet.Open(ui.Window);

        var outcomes = Receive(ui, auto: true, server.Url);

        Assert.Equal(ReceiptState.PendingConfirmation, Assert.Single(outcomes).State);
        Assert.IsType<SettingsSheet>(ui.Window.CurrentSheet);
        ui.Window.CloseSheet();
        Assert.False(ui.Sheet<AddSheet>().IsAuto); // vorgemerkt, nun im gewöhnlichen Dialog
        Assert.Empty(ui.Downloads.Items);
    }

    [AvaloniaFact]
    public async Task Links_added_while_checking_turn_it_into_a_normal_dialog()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);

        var outcomes = Receive(ui, auto: true, server.Url + "?handy");
        var sheet = ui.Sheet<AddSheet>();
        Assert.True(sheet.IsAuto);
        ui.Window.ReceiveLinks(server.Url + "?browser"); // etwa aus der Erweiterung

        Assert.Equal(ReceiptState.PendingConfirmation, (await Outcome(outcomes)).State);
        await Task.Delay(600);
        Ui.Pump();
        Assert.Empty(ui.Downloads.Items);
        Assert.Equal(2, sheet.Rows.OfType<AddCandidate>().Count());
    }

    [AvaloniaFact]
    public async Task A_key_press_hands_the_dialog_to_the_user()
    {
        using var ui = new Ui();
        using var server = new TestServer(1000);
        AllowTestServer(ui, server);

        var outcomes = Receive(ui, auto: true, server.Url);
        var sheet = ui.Sheet<AddSheet>();
        sheet.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });

        Assert.Equal(ReceiptState.PendingConfirmation, (await Outcome(outcomes)).State);
        await Task.Delay(600);
        Ui.Pump();
        Assert.Empty(ui.Downloads.Items);
        Assert.True(ui.Window.IsSheetOpen);
    }

    [AvaloniaFact]
    public async Task Several_requests_are_checked_one_after_the_other()
    {
        using var ui = new Ui();
        // Langsam: Der erste Download legt seine Datei noch nicht ab, sonst hieße der zweite „schon vorhanden“.
        using var server = new TestServer(200_000) { ThrottleBytesPerSecond = 20_000 };
        AllowTestServer(ui, server);

        var first = Receive(ui, auto: true, server.Url + "?1");
        var second = Receive(ui, auto: true, server.Url + "?2", server.Url + "?1");

        Assert.Equal(new RemoteOutcome(ReceiptState.Queued, 1), await Outcome(first));
        Assert.Equal(new RemoteOutcome(ReceiptState.Queued, 1, 1), await Outcome(second));
        Assert.Equal(2, ui.Downloads.Items.Count);
    }

    [AvaloniaFact]
    public void Switched_off_there_is_no_connection()
    {
        using var ui = new Ui();
        ui.App.ApplyRemote();
        SettingsSheet.Open(ui.Window);
        Ui.Click(ui.Sheet<SettingsSheet>(), "SaveButton");

        Assert.Equal(0, ui.Relays);
        Assert.Empty(ui.Relay.Subscriptions);
    }

    [AvaloniaFact]
    public async Task Pairing_in_the_settings_stores_the_phone_and_links_reach_the_dialog()
    {
        using var ui = new Ui();
        SettingsSheet.Open(ui.Window);
        var settings = ui.Sheet<SettingsSheet>();
        Assert.Equal(0, ui.Relays);

        Ui.Click(settings, "PairButton");
        Assert.Equal(1, ui.Relays);
        Assert.True(ui.Downloads.Settings.Remote.Enabled);
        Assert.NotNull(Ui.Find<Image>(settings, "PairQr").Source);
        byte[] seed = RemoteCrypto.DecodeCode(Ui.Find<SelectableTextBlock>(settings, "PairCode").Text)!;
        var phone = new FakePhone(seed);
        await Ui.WaitUntil(() => ui.Relay.OpenStreams == 1, "Abo");

        phone.Send(ui.Relay, new RemoteMessage { T = MessageType.Hello, Name = "Testhandy" });
        await Ui.WaitUntil(() => ui.Downloads.Settings.Remote.Devices.Count == 1, "Kopplung");
        await Ui.WaitUntil(() => phone.Replies(ui.Relay).Any(r => r.T == MessageType.HelloAck), "hello-ack");
        var device = ui.Downloads.Settings.Remote.Devices[0];
        Assert.Equal(("Testhandy", phone.Keys.DeviceId), (device.Name, device.DeviceId));
        Assert.StartsWith("v1:", device.SeedProtected);
        await Ui.WaitUntil(() => !Ui.Find<Control>(settings, "PairPanel").IsVisible, "Fenster zu");
        Assert.Single(Ui.Find<StackPanel>(settings, "DeviceList").Children);

        ui.Window.CloseSheet();
        var sent = phone.Links(ui.Relay, "https://example.invalid/datei.zip");
        await Ui.WaitUntil(() => ui.Window.CurrentSheet is AddSheet, "Dialog");
        await Ui.WaitUntil(() => phone.Replies(ui.Relay).Any(r => r.Ref == sent.Id), "Quittung");
        Assert.Equal(ReceiptState.PendingConfirmation, phone.Replies(ui.Relay).Single(r => r.Ref == sent.Id).State);

        ui.App.RemoveRemoteDevice(device.DeviceId);
        await Ui.WaitUntil(() => phone.Replies(ui.Relay).Any(r => r.T == MessageType.Bye) && ui.Relay.OpenStreams == 0, "Widerruf");
        Assert.Empty(ui.Downloads.Settings.Remote.Devices);
    }

    [AvaloniaFact]
    public async Task Closing_the_settings_cancels_the_pairing()
    {
        using var ui = new Ui();
        SettingsSheet.Open(ui.Window);
        var settings = ui.Sheet<SettingsSheet>();
        Ui.Click(settings, "PairButton");
        await Ui.WaitUntil(() => ui.Relay.OpenStreams == 1, "Abo");

        ui.Window.CloseSheet();
        Ui.Pump();

        Assert.Null(ui.App.Remote!.Pairing);
        await Ui.WaitUntil(() => ui.Relay.OpenStreams == 0, "Abo beendet");
    }

    [AvaloniaFact]
    public void Removing_phones_asks_first()
    {
        using var ui = new Ui();
        var devices = ui.Downloads.Settings.Remote.Devices;
        foreach (string id in new[] { "a", "b", "c" })
            devices.Add(new RemoteDevice { DeviceId = id, Name = "Handy " + id, PairedUtc = DateTime.UtcNow });
        SettingsSheet.Open(ui.Window);
        var settings = ui.Sheet<SettingsSheet>();
        var panel = Ui.Find<Control>(settings, "RemovePanel");
        Button First() => ((Grid)Ui.Find<StackPanel>(settings, "DeviceList").Children[0]).Children.OfType<Button>().Single();

        Ui.Click(First());
        Assert.True(panel.IsVisible);
        Assert.Contains("Handy a", Ui.Find<TextBlock>(settings, "RemoveQuestion").Text);
        Assert.Equal(3, devices.Count);
        Ui.Click(settings, "RemoveCancelButton");
        Assert.False(panel.IsVisible);
        Assert.Equal(3, devices.Count);

        Ui.Click(First());
        Ui.Click(settings, "RemoveConfirmButton");
        Assert.Equal(new[] { "b", "c" }, devices.Select(d => d.DeviceId));
        Assert.False(panel.IsVisible);

        Ui.Click(settings, "RemoveAllButton");
        Assert.Equal(2, devices.Count);
        Ui.Click(settings, "RemoveConfirmButton");
        Assert.Empty(devices);
        Assert.Same(settings, ui.Window.CurrentSheet);
    }
}
