using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using D6Downloader.Controls;
using D6Downloader.Downloads;
using D6Downloader.Engine;
using D6Downloader.Power;
using D6Downloader.Sheets;
using Xunit;

namespace D6Downloader.UiTests;

public class MainWindowTests
{
    private static DownloadItem Item(Ui ui, string name, DownloadStatus status, string? package = null) =>
        new("https://example.invalid/" + name, name, ui.Dir.Path) { Status = status, PackageId = package, PackageName = package };

    private static ListBox List(Ui ui) => Ui.Find<ListBox>(ui.Window, "List");

    [AvaloniaFact]
    public void The_window_opens_empty_with_sidebar_and_rapidgator_entry()
    {
        using var ui = new Ui();

        Assert.True(Ui.Find<Control>(ui.Window, "EmptyState").IsVisible);
        Assert.True(Ui.Find<Button>(ui.Window, "RapidgatorButton").IsEffectivelyVisible);
        var texts = Ui.Texts(ui.Window);
        Assert.Contains("Rapidgator", texts);
        Assert.Contains(Loc.T("Sidebar.All"), texts);
        Assert.Contains(Loc.T("Main.Empty.Title"), texts);
    }

    [AvaloniaFact]
    public void Rows_are_ordered_by_status_and_packages_get_a_head()
    {
        using var ui = new Ui();
        var done = Item(ui, "fertig.zip", DownloadStatus.Completed);
        var one = Item(ui, "film.part1.rar", DownloadStatus.Paused, "film");
        var two = Item(ui, "film.part2.rar", DownloadStatus.Paused, "film");

        // Add setzt „Angehalten“; der fertige bekommt seinen Zustand danach, wie beim Abschluss.
        ui.Downloads.Add(new[] { done, one, two }, start: false);
        done.Status = DownloadStatus.Completed;
        Ui.Pump();

        var rows = ui.Window.Rows.Rows.ToList();
        var head = Assert.IsType<PackageHead>(rows[0]);
        Assert.Equal("film", head.Name);
        Assert.Same(done, rows[1]);
        Assert.False(Ui.Find<Control>(ui.Window, "EmptyState").IsVisible);
        var texts = Ui.Texts(List(ui));
        Assert.Contains("film", texts);
        Assert.Contains("fertig.zip", texts);

        // Seitenleiste: nur Fertige.
        var sidebar = Ui.Find<ListBox>(ui.Window, "SidebarList");
        sidebar.SelectedItem = sidebar.Items.OfType<SidebarEntry>().First(e => e.Kind == SidebarKind.Completed);
        Ui.Pump();
        Assert.Same(done, Assert.Single(ui.Window.Rows.Rows));
    }

    [AvaloniaFact]
    public void Selection_survives_resorting()
    {
        using var ui = new Ui();
        var first = Item(ui, "a.bin", DownloadStatus.Paused);
        var second = Item(ui, "b.bin", DownloadStatus.Paused);
        ui.Downloads.Add(new[] { first, second }, start: false);
        Ui.Pump();
        List(ui).SelectedItems!.Add(first);

        first.Status = DownloadStatus.Completed;
        Ui.Pump();

        Assert.Equal(new object[] { second, first }, ui.Window.Rows.Rows.ToArray());
        Assert.Same(first, Assert.Single(List(ui).SelectedItems!.Cast<object>()));
    }

    [AvaloniaFact]
    public void The_add_sheet_probes_local_links_and_creates_downloads()
    {
        using var ui = new Ui();
        using var server = new Tests.TestServer(4000);

        ui.Window.ReceiveLinks("Bitte laden: " + server.Url + " danke");
        var sheet = ui.Sheet<AddSheet>();
        var candidate = Assert.IsType<AddCandidate>(Assert.Single(sheet.Rows));
        Assert.Null(candidate.ServiceId);
    }

    [AvaloniaFact]
    public async Task Adding_takes_the_probed_candidate_into_the_list()
    {
        using var ui = new Ui();
        using var server = new Tests.TestServer(4000);
        ui.Window.OpenAdd(server.Url);
        var sheet = ui.Sheet<AddSheet>();
        var candidate = Assert.IsType<AddCandidate>(Assert.Single(sheet.Rows));
        await Ui.WaitUntil(() => candidate.State == CandidateState.Ready, "Probe");
        Assert.Equal(4000, candidate.Size);

        var add = Ui.Find<Button>(sheet, "AddButton");
        await Ui.WaitUntil(() => add.IsEffectivelyEnabled, "Knopf");
        Ui.Click(add);

        Assert.False(ui.Window.IsSheetOpen);
        var item = Assert.Single(ui.Downloads.Items);
        Assert.Equal(server.Url, item.Url);
        Assert.Equal(ui.Dir.Path, item.Folder);
        Assert.Null(item.ViaService);
        Assert.Equal(DownloadStatus.Paused, item.Status);
    }

    [AvaloniaFact]
    public void Links_arriving_during_another_sheet_wait_until_it_closes()
    {
        using var ui = new Ui();
        SettingsSheet.Open(ui.Window);

        ui.Window.ReceiveLinks("http://127.0.0.1:1/a.bin");
        Assert.IsType<SettingsSheet>(ui.Window.CurrentSheet);

        ui.Window.CloseSheet();
        var sheet = ui.Sheet<AddSheet>();
        Assert.Single(sheet.Rows);
    }

    [AvaloniaFact]
    public async Task Every_sheet_opens_and_lays_out()
    {
        using var ui = new Ui(s => s.Queues.Add(new QueueSetting { Id = "q", Name = "Nacht", Schedule = new ScheduleSetting() }));
        var item = Item(ui, "x.zip", DownloadStatus.Paused);
        ui.Downloads.Add(new[] { item }, start: false);
        Ui.Pump();

        void Shown<T>(Action open) where T : Control
        {
            open();
            var sheet = ui.Sheet<T>();
            Assert.True(sheet.Bounds.Height > 50, typeof(T).Name);
            ui.Window.CloseSheet();
        }

        Shown<SettingsSheet>(() => SettingsSheet.Open(ui.Window));
        Shown<DebridSheet>(() => DebridSheet.Open(ui.Window));
        Shown<HostsSheet>(() => HostsSheet.Open(ui.Window));
        Shown<BrowserSheet>(() => BrowserSheet.Open(ui.Window));
        Shown<CategoriesSheet>(() => CategoriesSheet.Open(ui.Window));
        Shown<QueuesSheet>(() => QueuesSheet.Open(ui.Window));
        Shown<EditSheet>(() => EditSheet.Open(ui.Window, new() { item }));
        Shown<EditSheet>(() => EditSheet.Open(ui.Window, new() { item, item }));
        Shown<DetailSheet>(() => DetailSheet.Open(ui.Window, item));
        Shown<PasswordSheet>(() => PasswordSheet.Open(ui.Window, new[] { item }));
        Shown<RapidgatorSheet>(() => RapidgatorSheet.Open(ui.Window));
        Shown<ConfirmSheet>(() => _ = ConfirmSheet.AskAsync(ui.Window, "Frage", "Text", "Ja"));

        AfterSheet.Open(ui.Window);
        var after = ui.Sheet<AfterSheet>();
        await after.Loaded;
        Ui.Pump();
        Assert.True(Ui.Find<Button>(after, "ApplyButton").IsEnabled);
        Assert.Equal(3, Ui.Find<ComboBox>(after, "ActionBox").ItemCount); // nichts, Ausschalten, Energie sparen
        ui.Window.CloseSheet();
    }

    [AvaloniaFact]
    public async Task Settings_are_applied_saved_and_use_the_injected_autostart()
    {
        using var ui = new Ui();
        SettingsSheet.Open(ui.Window);
        var sheet = ui.Sheet<SettingsSheet>();
        Assert.Contains("Yusa", Ui.Find<SelectableTextBlock>(sheet, "AboutText").Text);
        Ui.Find<CheckBox>(sheet, "AutostartBox").IsChecked = true;
        Ui.Find<CheckBox>(sheet, "KeepAwakeBox").IsChecked = false;

        Ui.Click(sheet, "SaveButton");
        await Ui.WaitUntil(() => !ui.Window.IsSheetOpen, "Speichern");

        Assert.True(ui.Autostart.Enabled);
        Assert.False(ui.Downloads.Settings.KeepAwake);
        Assert.Contains("\"keepAwake\": false", File.ReadAllText(AppPaths.SettingsFile));
    }

    [AvaloniaFact]
    public async Task Deleting_permanently_is_only_available_together_with_removing_archives()
    {
        using var ui = new Ui();
        SettingsSheet.Open(ui.Window);
        var sheet = ui.Sheet<SettingsSheet>();
        var remove = Ui.Find<CheckBox>(sheet, "DeleteArchivesBox");
        var permanently = Ui.Find<CheckBox>(sheet, "DeletePermanentlyBox");
        Assert.False(permanently.IsEnabled);
        Assert.False(permanently.IsChecked);

        remove.IsChecked = true;
        Assert.True(permanently.IsEnabled);
        Assert.False(permanently.IsChecked);
        permanently.IsChecked = true;
        remove.IsChecked = false;
        Assert.False(permanently.IsEnabled);
        Assert.False(permanently.IsChecked);

        remove.IsChecked = true;
        permanently.IsChecked = true;
        Ui.Click(sheet, "SaveButton");
        await Ui.WaitUntil(() => !ui.Window.IsSheetOpen, "Speichern");
        Assert.True(ui.Downloads.Settings.DeleteArchivesAfterExtract);
        Assert.True(ui.Downloads.Settings.DeleteArchivesPermanently);

        SettingsSheet.Open(ui.Window);
        sheet = ui.Sheet<SettingsSheet>();
        Assert.True(Ui.Find<CheckBox>(sheet, "DeletePermanentlyBox").IsEnabled);
        Assert.True(Ui.Find<CheckBox>(sheet, "DeletePermanentlyBox").IsChecked);
        Ui.Find<CheckBox>(sheet, "DeleteArchivesBox").IsChecked = false;
        Ui.Click(sheet, "SaveButton");
        await Ui.WaitUntil(() => !ui.Window.IsSheetOpen, "Speichern");
        Assert.False(ui.Downloads.Settings.DeleteArchivesAfterExtract);
        Assert.False(ui.Downloads.Settings.DeleteArchivesPermanently);
    }

    [AvaloniaFact]
    public void The_browser_is_registered_only_on_click_and_only_in_the_given_folder()
    {
        using var ui = new Ui();
        BrowserSheet.Open(ui.Window);
        var sheet = ui.Sheet<BrowserSheet>();
        string manifest = ui.Dir.File("chrome/NativeMessagingHosts/d6downloader.host.json");
        Assert.False(File.Exists(manifest));
        Assert.False(Ui.Find<Button>(sheet, "FirefoxButton").IsEnabled); // kein Profilordner: kein toter Klick

        Ui.Click(sheet, "ChromeButton");

        Assert.Contains("/opt/d6downloader", File.ReadAllText(manifest));
    }

    [AvaloniaFact]
    public void The_countdown_can_be_cancelled_and_nothing_is_executed()
    {
        using var ui = new Ui();
        ui.Downloads.AfterCompletion.Arm(PowerAction.Shutdown);
        ui.Downloads.AfterCompletion.NotifyAllDone();
        var sheet = ui.Sheet<CountdownSheet>();

        Ui.Click(sheet, "CancelButton");

        Assert.False(ui.Window.IsSheetOpen);
        Assert.False(ui.Downloads.AfterCompletion.IsArmed);
        Assert.Empty(ui.Power.Executed);
    }

    [AvaloniaFact]
    public void Closing_without_a_tray_quits_instead_of_hiding_the_window()
    {
        using var ui = new Ui();
        Assert.False(ui.App.CanHideToTray);

        ui.Window.Close();
        Ui.Pump();

        Assert.True(ui.App.Quitting);
    }

    [AvaloniaFact]
    public void Typed_rows_reach_the_settings()
    {
        using var ui = new Ui();
        QueuesSheet.Open(ui.Window);
        var sheet = ui.Sheet<QueuesSheet>();
        Ui.Click(sheet.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T("Queues.AddRow"))));
        var name = sheet.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == Loc.T("Queues.NewName"));
        name.Text = "Nachts";
        Ui.Pump();

        Ui.Click(sheet.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T("Common.Apply"))));

        Assert.Equal("Nachts", Assert.Single(ui.Downloads.Settings.Queues).Name);
        var sidebar = Ui.Find<ListBox>(ui.Window, "SidebarList");
        Assert.Contains(sidebar.Items.OfType<SidebarEntry>(), e => e.Title == "Nachts");
    }

    [AvaloniaFact]
    public void The_row_menu_offers_what_fits_the_selection()
    {
        using var ui = new Ui();
        var item = Item(ui, "a.zip", DownloadStatus.Paused);
        ui.Downloads.Add(new[] { item }, start: false);
        Ui.Pump();
        Ui.Find<ListBox>(ui.Window, "List").SelectedItems!.Add(item);

        ui.Window.PrepareMenu(new() { item });

        var menu = Ui.Find<ListBox>(ui.Window, "List").ContextMenu!;
        var visible = menu.Items.OfType<MenuItem>().Where(m => m.IsVisible).Select(m => m.Name).ToList();
        Assert.Contains("MenuStart", visible);
        Assert.DoesNotContain("MenuPause", visible);
        Assert.Contains("MenuExtract", visible);
        Assert.DoesNotContain("MenuQueue", visible);
        var categories = menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuCategory");
        Assert.True(categories.Items.Count > 2);
    }

    [AvaloniaFact]
    public void Messages_from_the_socket_show_the_window_and_open_the_links()
    {
        using var ui = new Ui();
        var inbox = new Services.IpcInbox();
        inbox.Links("http://127.0.0.1:1/a.bin"); // kommt an, bevor die Oberfläche steht

        inbox.Attach(message => ui.App.OnMessage(message));

        Assert.Single(ui.Sheet<AddSheet>().Rows);
    }

    [AvaloniaFact]
    public void The_row_bar_fills_without_gaps_up_to_the_overall_progress()
    {
        using var ui = new Ui();
        var item = Item(ui, "gross.iso", DownloadStatus.Paused);
        item.TotalBytes = 4000;
        ui.Downloads.Add(new[] { item }, start: false);
        item.BeginRun();
        // Vier Verbindungen, jede erst teilweise fertig: zusammen 40 %.
        ((IProgress<DownloadProgress>)item).Report(new DownloadProgress(4000, 1600, 0,
            new long[] { 600, 200, 600, 200 }, new long[] { 1000, 1000, 1000, 1000 }));
        item.Tick(Environment.TickCount64);
        Ui.Pump();
        Ui.Pump();

        var bar = Assert.Single(List(ui).GetVisualDescendants().OfType<SegmentBar>(), b => b.IsEffectivelyVisible);
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(bar.Fill).Color;
        using var frame = ui.Window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var origin = bar.TranslatePoint(new Point(0, 0), ui.Window)!.Value;
        double scale = ui.Window.RenderScaling;
        int left = (int)Math.Ceiling(origin.X * scale), width = (int)(bar.Bounds.Width * scale);
        int y = (int)((origin.Y + bar.Bounds.Height / 2) * scale);
        Color Pixel(int x)
        {
            int value = Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + (left + x) * 4);
            byte b0 = (byte)value, b1 = (byte)(value >> 8), b2 = (byte)(value >> 16);
            return buffer.Format == PixelFormat.Rgba8888 ? Color.FromRgb(b0, b1, b2) : Color.FromRgb(b2, b1, b0);
        }

        Assert.Equal(0.4, item.Fraction, 3);
        int end = (int)(width * item.Fraction);
        var track = Pixel(width - 4);
        Assert.NotEqual(fill, track);
        // Kleiner Abstand zu den runden Enden und zur Kante der Füllung.
        for (int x = 3; x < end - 3; x++) Assert.True(Pixel(x) == fill, $"Lücke bei {x} von {width}");
        for (int x = end + 3; x < width - 3; x++) Assert.True(Pixel(x) == track, $"Füllung hinter dem Fortschritt bei {x} von {width}");
    }
}
