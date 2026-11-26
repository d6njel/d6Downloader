using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using D6Downloader.Downloads;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Packages;
using D6Downloader.Remote;
using D6Downloader.Sheets;

namespace D6Downloader;

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly DownloadManager _downloads;
    private readonly Sidebar _sidebar;
    private readonly PackageGrouping _grouping = new();
    private readonly DownloadRows _rows;
    private SidebarEntry _current;
    private List<object> _selection = new();
    private int _lastCount;
    private bool _pointerDown;
    private bool _menuOpen;
    private double _scale = 1;

    /// <summary>Nur für den XAML-Lader.</summary>
    public MainWindow() => throw new NotSupportedException();

    public MainWindow(AppController app)
    {
        _app = app;
        _downloads = app.Downloads;
        InitializeComponent();
        DataContext = _downloads;
        _sidebar = new Sidebar();

        SidebarList.ItemsSource = _sidebar.Entries;
        SidebarList.ContainerPrepared += (_, e) =>
        {
            bool header = e.Container.DataContext is SidebarEntry { IsHeader: true }
                || (e.Index >= 0 && e.Index < _sidebar.Entries.Count && _sidebar.Entries[e.Index].IsHeader);
            e.Container.Classes.Set("header", header);
            e.Container.Focusable = !header;
        };
        _current = _sidebar.Rebuild(_downloads.Settings.EffectiveCategories, _downloads.Settings.Queues, null);
        SidebarList.SelectedItem = _current;

        _rows = new DownloadRows(_downloads.Items, i => _current.Matches(i), _grouping,
            () => _menuOpen || _pointerDown, a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background));
        _rows.Rebuilding += () => _selection = List.SelectedItems?.Cast<object>().ToList() ?? new();
        _rows.Rebuilt += RestoreSelection;
        List.ItemsSource = _rows.Rows;

        _downloads.ListChanged += (_, _) => OnListChanged();
        _downloads.Ticked += (_, _) => _grouping.UpdateRunning();
        _downloads.AfterCompletion.CountdownStarted += (_, _) => CountdownSheet.Open(this, _downloads.AfterCompletion);
        _downloads.AfterCompletion.PropertyChanged += OnAfterCompletionChanged;
        _downloads.RapidgatorChanged += (_, _) => UpdateRapidgatorDot();

        List.AddHandler(KeyDownEvent, List_KeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(DoubleTappedEvent, List_DoubleTapped);
        List.AddHandler(PointerPressedEvent, (_, _) => _pointerDown = true, RoutingStrategies.Tunnel, true);
        List.AddHandler(PointerReleasedEvent, (_, _) => PointerDone(), RoutingStrategies.Tunnel, true);
        List.AddHandler(PointerCaptureLostEvent, (_, _) => PointerDone(), RoutingStrategies.Direct | RoutingStrategies.Bubble, true);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, Window_DragOver);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
        AddHandler(DragDrop.DropEvent, Window_Drop);

        UpdateEmptyState();
        UpdateRapidgatorDot();
    }

    public AppController App => _app;
    public DownloadManager Downloads => _downloads;

    /// <summary>Die Zeilen der Liste (Downloads und Paketköpfe).</summary>
    internal DownloadRows Rows => _rows;

    // ---------------------------------------------------------------- Hinweise auf Datenprobleme

    // Nacheinander, erst nach dem ersten Anzeigen und nie über einem offenen Sheet.
    private bool _noticesReady;
    private bool _noticeShowing;

    public void StartNotices()
    {
        _noticesReady = true;
        _downloads.NoticesChanged += (_, _) => ShowNextNotice();
        ShowNextNotice();
    }

    private async void ShowNextNotice()
    {
        if (!_noticesReady || _noticeShowing || IsSheetOpen || !_downloads.Notices.TryDequeue(out var notice)) return;
        _noticeShowing = true;
        Reveal();
        try { await ConfirmSheet.TellAsync(this, notice.Title, notice.Message); }
        finally { _noticeShowing = false; }
        ShowNextNotice();
    }

    // ---------------------------------------------------------------- Seitenleiste und Liste

    private void OnListChanged()
    {
        _grouping.UpdateAll(); // Zustandswechsel und Entpacken ändern die Köpfe auch ohne Takt
        RefreshCategories();
        // Neu hinzugefügt, aber in der gewählten Ansicht unsichtbar: zu „Alle“ wechseln, sonst wirkt es verschluckt.
        var items = _downloads.Items;
        if (items.Count > _lastCount && !_current.Matches(items[^1])) SidebarList.SelectedItem = _sidebar.All;
        _lastCount = items.Count;

        _sidebar.Recount(items);
        _rows.RequestRefresh();
        UpdateEmptyState();
    }

    private void RefreshCategories()
    {
        // Kategorien und Queues neu aufbauen, nur wenn sie sich geändert haben (sonst springt die Auswahl).
        var settings = _downloads.Settings;
        if (!_sidebar.IsCurrent(settings.EffectiveCategories, settings.Queues))
        {
            var next = _sidebar.Rebuild(settings.EffectiveCategories, settings.Queues, _current);
            _current = next;
            SidebarList.SelectedItem = next;
            _rows.RequestRefresh();
        }
    }

    private void Sidebar_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is not SidebarEntry entry) return;
        if (entry.IsHeader)
        {
            // Überschriften sind keine Ansicht (Tastatur): zurück zur bisherigen Auswahl.
            Dispatcher.UIThread.Post(() => SidebarList.SelectedItem = _current);
            return;
        }
        if (entry == _current) return;
        _current = entry;
        _rows?.Refresh();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool none = _downloads.Items.Count == 0;
        bool empty = none || !_downloads.Items.Any(_current.Matches);
        EmptyState.IsVisible = empty;
        EmptyTitle.Text = none ? Loc.T("Main.Empty.Title") : Loc.T("Main.Empty.NothingIn", _current.Title);
        EmptyHint.IsVisible = none;
    }

    private void RestoreSelection()
    {
        if (List.SelectedItems is not { } selected || _selection.Count == 0) return;
        foreach (object row in _selection)
            if (!selected.Contains(row) && _rows.Rows.Contains(row)) selected.Add(row);
    }

    private void PointerDone()
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _rows.RequestRefresh(); // eine zurückgestellte Umsortierung nachholen
    }

    private void SidebarEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (IsSheetOpen || (sender as Control)?.DataContext is not SidebarEntry entry) return;
        if (entry.Edit == SidebarEdit.Categories) CategoriesSheet.Open(this);
        else if (entry.Edit == SidebarEdit.Queues) QueuesSheet.Open(this);
    }

    // ---------------------------------------------------------------- Fenster, Skalierung

    /// <summary>Skaliert den Inhalt. Das Fenster wächst mit (Menüs und Tooltips folgen der Skalierung des Systems).</summary>
    public void ApplyScale(double scale)
    {
        if (scale == _scale) return;
        double ratio = scale / _scale;
        _scale = scale;
        Scaler.LayoutTransform = scale == 1 ? null : new ScaleTransform(scale, scale);

        double maxWidth = double.PositiveInfinity, maxHeight = double.PositiveInfinity;
        if (Screens.Primary is { } screen)
        {
            maxWidth = screen.WorkingArea.Width / screen.Scaling;
            maxHeight = screen.WorkingArea.Height / screen.Scaling;
        }
        MinWidth = Math.Min(700 * scale, maxWidth);
        MinHeight = Math.Min(480 * scale, maxHeight);
        if (WindowState != WindowState.Normal) return;
        Width = Math.Min(Width * ratio, maxWidth);
        Height = Math.Min(Height * ratio, maxHeight);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        bool forced = _app.Quitting
            || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
        if (forced) return;
        if (_downloads.Settings.CloseAction == CloseAction.Tray && _app.CanHideToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // Schließen beendet das Programm, das Beenden übernimmt der Controller.
        e.Cancel = true;
        Dispatcher.UIThread.Post(_app.Quit);
    }

    /// <summary>Holt das Fenster aus dem Tray oder der Taskleiste nach vorn (unter Wayland kann der Fokusschutz das verhindern).</summary>
    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    // ---------------------------------------------------------------- Sheets

    public bool IsSheetOpen => SheetLayer.IsVisible;

    /// <summary>Das gerade sichtbare Sheet, sonst null.</summary>
    public Control? CurrentSheet => SheetHost.Content as Control;

    /// <summary>Zeigt ein Sheet modal unter der Werkzeugleiste. Liste und Werkzeuge sind solange gesperrt.</summary>
    public void ShowSheet(Control sheet, Control? focus = null)
    {
        SheetHost.Content = sheet;
        SheetLayer.IsVisible = true;
        Main.IsEnabled = false;
        Toolbar.IsEnabled = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (SheetHost.Content == sheet) (focus ?? SheetHost).Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>Schließt das Sheet nur, wenn gerade dieses zu sehen ist.</summary>
    public void CloseSheetIfShowing(Control sheet)
    {
        if (SheetHost.Content == sheet) CloseSheet();
    }

    public void CloseSheet()
    {
        SheetLayer.IsVisible = false;
        SheetHost.Content = null;
        Main.IsEnabled = true;
        Toolbar.IsEnabled = true;
        List.Focus();
        if (_pendingLinks is { } pending)
        {
            var passwords = _pendingPasswords;
            _pendingLinks = null;
            _pendingPasswords = new();
            OpenAdd(pending, passwords);
        }
        else if (!_noticeShowing) ShowNextNotice(); // zurückgestellte Hinweise
    }

    private string? _pendingLinks;
    private List<string> _pendingPasswords = new();

    /// <summary>
    /// Links aus der Browser-Erweiterung: Fenster nach vorn, Hinzufügen-Dialog mit den Links. Ist gerade ein anderes Sheet offen,
    /// bleiben die Links vorgemerkt, bis es zu ist, und gehen nicht verloren.
    /// </summary>
    public void ReceiveLinks(string text, IReadOnlyList<string>? passwords = null)
    {
        if (!LinkParser.ContainsLink(text)) return;
        Reveal();
        if (IsSheetOpen && SheetHost.Content is not AddSheet)
        {
            _pendingLinks = _pendingLinks is null ? text : _pendingLinks + Environment.NewLine + text;
            if (passwords is not null) _pendingPasswords = PasswordRules.Normalize(_pendingPasswords.Concat(passwords));
        }
        else
            OpenAdd(text, passwords);
    }

    // ---------------------------------------------------------------- Links vom Handy

    private readonly Queue<(string Text, Action<RemoteOutcome> Done)> _remoteQueue = new();
    private bool _remoteBusy;

    /// <summary>Prüfung der Ziele für „sofort starten“.</summary>
    internal TargetGuard RemoteTargets { get; set; } = new();

    /// <summary>So lange wartet „sofort starten“ höchstens auf Probe und Online-Check.</summary>
    internal TimeSpan RemoteTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Links vom Handy. Ohne <paramref name="auto"/> der gewöhnliche Weg in den Dialog. Mit <paramref name="auto"/> sofort
    /// starten, aber nie, während der Benutzer in einem Sheet arbeitet. <paramref name="done"/> wird genau einmal gerufen.
    /// </summary>
    public void ReceiveRemoteLinks(string text, bool auto, Action<RemoteOutcome> done)
    {
        int count = LinkParser.Extract(text).Count;
        if (count == 0)
        {
            done(new RemoteOutcome(ReceiptState.Rejected, 0, Err: ReceiptError.NoLink));
            return;
        }
        if (!auto || (!_remoteBusy && IsSheetOpen))
        {
            ReceiveLinks(text);
            done(new RemoteOutcome(ReceiptState.PendingConfirmation, count));
            return;
        }
        _remoteQueue.Enqueue((text, done));
        if (!_remoteBusy) RunRemoteQueue();
    }

    private async void RunRemoteQueue()
    {
        _remoteBusy = true;
        try
        {
            while (_remoteQueue.TryDequeue(out var job))
            {
                var outcome = await StartRemoteAsync(job.Text);
                job.Done(outcome);
            }
        }
        finally
        {
            _remoteBusy = false;
        }
    }

    private async Task<RemoteOutcome> StartRemoteAsync(string text)
    {
        var allowed = new List<string>();
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        int blocked = 0;
        try
        {
            // Vor der Probe prüfen: Schon sie wäre eine Anfrage an das Ziel.
            foreach (string url in LinkParser.Extract(text))
            {
                var verdict = await RemoteTargets.CheckAsync(url);
                if (verdict == TargetVerdict.Blocked)
                {
                    blocked++;
                    continue;
                }
                allowed.Add(url);
                if (verdict == TargetVerdict.Unresolved) unresolved.Add(url);
            }
            if (allowed.Count == 0) return new RemoteOutcome(ReceiptState.Rejected, 0, blocked, ReceiptError.BlockedTarget);
            string links = string.Join(Environment.NewLine, allowed);
            if (_app.Quitting || IsSheetOpen)
            {
                if (!_app.Quitting) ReceiveLinks(links);
                return new RemoteOutcome(ReceiptState.PendingConfirmation, allowed.Count, blocked);
            }
            var finished = new TaskCompletionSource<RemoteOutcome>();
            AddSheet.OpenAuto(this, links, RemoteTargets, unresolved, RemoteTimeout, o => finished.TrySetResult(o));
            var outcome = await finished.Task;
            return outcome with { Skipped = outcome.Skipped + blocked };
        }
        catch (Exception)
        {
            // Im Zweifel nichts starten.
            ReceiveLinks(text);
            return new RemoteOutcome(ReceiptState.PendingConfirmation, allowed.Count);
        }
    }

    /// <summary>Öffnet den Hinzufügen-Dialog. Ist er schon offen, kommen die Links dazu.</summary>
    public void OpenAdd(string? text, IReadOnlyList<string>? passwords = null)
    {
        if (SheetHost.Content is AddSheet open)
        {
            if (!string.IsNullOrEmpty(text)) open.Append(text);
            open.AddPasswords(passwords);
            return;
        }
        if (IsSheetOpen) return; // eine andere Rückfrage ist offen
        var sheet = AddSheet.Open(this, text, _current.Kind == SidebarKind.Category ? _current.Category : null,
            _current.Kind == SidebarKind.Queue ? _current.QueueId : null);
        sheet.AddPasswords(passwords);
    }

    // ---------------------------------------------------------------- Werkzeugleiste und Tasten

    private async void Add_Click(object? sender, RoutedEventArgs e) => await OpenAddFromClipboard();
    private void StartAll_Click(object? sender, RoutedEventArgs e) => _downloads.StartAll();
    private void PauseAll_Click(object? sender, RoutedEventArgs e) => _downloads.PauseAll();
    private void RemoveCompleted_Click(object? sender, RoutedEventArgs e) => _downloads.RemoveCompleted();

    private void After_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen) AfterSheet.Open(this);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen) SettingsSheet.Open(this);
    }

    private void Rapidgator_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen) RapidgatorSheet.Open(this);
    }

    private void UpdateRapidgatorDot()
    {
        var state = _downloads.Rapidgator.State;
        RapidgatorDot.IsVisible = state != RapidgatorState.LoggedOut;
        RapidgatorDot.Classes.Set("online", state == RapidgatorState.LoggedIn && _downloads.Settings.Rapidgator.Enabled);
        RapidgatorDot.Classes.Set("warn", state is RapidgatorState.CodeRequired or RapidgatorState.LoginFailed);
    }

    /// <summary>Nach dem Rapidgator-Sheet: Der Punkt zeigt auch Änderungen ohne Ereignis (Schalter).</summary>
    internal void RefreshAccounts() => UpdateRapidgatorDot();

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsSheetOpen || FocusManager?.GetFocusedElement() is TextBox) return;
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (e.Key is Key.V or Key.N)
        {
            e.Handled = true;
            await OpenAddFromClipboard();
        }
        else if (e.Key == Key.OemComma)
        {
            e.Handled = true;
            SettingsSheet.Open(this);
        }
        else if (e.Key == Key.Q)
        {
            e.Handled = true;
            _app.Quit();
        }
    }

    internal async Task<string?> ClipboardText()
    {
        try
        {
            return Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        }
        catch (Exception)
        {
            return null; // Zwischenablage nicht lesbar
        }
    }

    internal async Task CopyText(string text)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Zwischenablage gerade belegt
        }
    }

    private async Task OpenAddFromClipboard()
    {
        string? text = await ClipboardText();
        OpenAdd(LinkParser.ContainsLink(text) ? text : null);
    }

    private async void List_KeyDown(object? sender, KeyEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        switch (e.Key)
        {
            case Key.Delete:
                e.Handled = true;
                RemoveAsk(selected);
                break;
            case Key.Space:
                e.Handled = true;
                ToggleAll(selected);
                break;
            case Key.Enter:
                // Köpfe öffnen keine Dateien (ein Paket hat viele Teile): Enter klappt sie auf und zu.
                e.Handled = true;
                var open = SelectedRows().Where(i => i.Status == DownloadStatus.Completed).ToList();
                foreach (var head in SelectedHeads()) ToggleHead(head);
                foreach (var item in open) OpenFile(item);
                break;
            case Key.Right or Key.Left when e.KeyModifiers == KeyModifiers.None && SelectedHeads().Count > 0:
                e.Handled = true;
                foreach (var head in SelectedHeads())
                    if (head.IsExpanded == (e.Key == Key.Left)) ToggleHead(head);
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                await CopyLinks(selected);
                break;
            case Key.F2:
                e.Handled = true;
                EditSheet.Open(this, selected);
                break;
            case Key.I when e.KeyModifiers == KeyModifiers.Control:
                if (selected.Count != 1) return; // Details nur für genau einen Download
                e.Handled = true;
                DetailSheet.Open(this, selected[0]);
                break;
        }
    }

    private void List_DoubleTapped(object? sender, TappedEventArgs e)
    {
        switch (RowAt(e.Source))
        {
            case DownloadItem item:
                DetailSheet.Open(this, item);
                break;
            case PackageHead head:
                ToggleHead(head);
                break;
        }
    }

    private static object? RowAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button) return null; // Klick auf einen Zeilenknopf
            if (v is ListBoxItem row) return row.DataContext;
        }
        return null;
    }

    private void RowAction_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DownloadItem item) return;
        if (item.Status == DownloadStatus.Completed) ShowInFolder(item);
        else _downloads.Toggle(item);
    }

    /// <summary>Zeilenknopf des Kopfs: alle Teile starten oder anhalten, bei fertigem Paket den Ordner zeigen.</summary>
    private void HeadAction_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PackageHead head || head.Members.Count == 0) return;
        if (head.Status == DownloadStatus.Completed) ShowInFolder(head.Members[0]);
        else ToggleAll(head.Members.ToList());
    }

    private void HeadToggle_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PackageHead head) ToggleHead(head);
    }

    private void ToggleHead(PackageHead head)
    {
        _grouping.Toggle(head);
        _rows.Refresh();
    }

    // ---------------------------------------------------------------- Drag & Drop

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        bool ok = (!IsSheetOpen || SheetHost.Content is AddSheet) && DropData.CanRead(e.DataTransfer);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.IsVisible = ok && !IsSheetOpen;
        e.Handled = true;
    }

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        if (IsSheetOpen && SheetHost.Content is not AddSheet) return;
        string? text = DropData.Read(e.DataTransfer);
        if (LinkParser.ContainsLink(text)) OpenAdd(text);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- Kontextmenü

    private void RowMenu_Opening(object? sender, CancelEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        _menuOpen = true;
        PrepareMenu(selected);
    }

    private void RowMenu_Closed(object? sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        _rows.RequestRefresh();
    }

    internal void PrepareMenu(List<DownloadItem> selected)
    {
        // Auch ein Download, der auf den Zeitplan seiner Queue wartet, lässt sich sofort starten.
        MenuStart.IsVisible = selected.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed
            || (i.Status == DownloadStatus.Queued && i.WaitNote is not null));
        MenuPause.IsVisible = selected.Any(i => i.Status is DownloadStatus.Running or DownloadStatus.Queued);
        StartSeparator.IsVisible = MenuStart.IsVisible || MenuPause.IsVisible;
        // Nur direkt markierte Downloads: Ein Paketkopf öffnet nicht alle Teile auf einmal.
        MenuOpen.IsEnabled = SelectedRows().Any(i => i.Status == DownloadStatus.Completed);
        MenuDetails.IsEnabled = selected.Count == 1;
        MenuExtract.IsVisible = selected.Any(_downloads.CanOfferExtract);
        MenuExtract.IsEnabled = selected.Any(_downloads.CanExtract);
        MenuPassword.IsVisible = selected.Any(_downloads.CanOfferExtract);
        MenuPassword.IsEnabled = selected.Any(_downloads.CanEnterPassword);
        MenuAfter.IsEnabled = selected.Count == 1 && selected[0].Status != DownloadStatus.Completed;
        FillCategoryMenu(selected);
        MenuQueue.IsVisible = _downloads.Settings.Queues.Count > 0; // ohne eigene Queues gibt es nichts zu wählen
        FillQueueMenu(selected);
    }

    /// <summary>Untermenü „Kategorie“: Haken bei der Kategorie, die alle markierten Downloads teilen.</summary>
    private void FillCategoryMenu(List<DownloadItem> selected)
    {
        MenuCategory.Items.Clear();
        string? shared = selected.All(i => i.Category == selected[0].Category) ? selected[0].Category : "\u0001";
        void Add(string header, string? category)
        {
            var entry = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = shared == category };
            entry.Click += (_, _) => _downloads.SetCategory(selected, category);
            MenuCategory.Items.Add(entry);
        }
        Add(Loc.T("Category.None"), null);
        if (_downloads.Settings.EffectiveCategories.Count > 0) MenuCategory.Items.Add(new Separator());
        foreach (var c in _downloads.Settings.EffectiveCategories) Add(c.Name, c.Key);
    }

    private void FillQueueMenu(List<DownloadItem> selected)
    {
        MenuQueue.Items.Clear();
        string? shared = selected.All(i => i.QueueId == selected[0].QueueId) ? selected[0].QueueId : "\u0001";
        void Add(string header, string? queueId)
        {
            var entry = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = shared == queueId };
            entry.Click += (_, _) => _downloads.SetQueue(selected, queueId);
            MenuQueue.Items.Add(entry);
        }
        Add(Loc.T("Queue.Default"), null);
        foreach (var q in _downloads.Settings.Queues) Add(q.Name, q.Id);
    }

    private void MenuStart_Click(object? sender, RoutedEventArgs e) => _downloads.Start(Selected());
    private void MenuPause_Click(object? sender, RoutedEventArgs e) => _downloads.Pause(Selected());

    private void MenuOpen_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in SelectedRows().Where(i => i.Status == DownloadStatus.Completed)) OpenFile(item);
    }

    private void MenuShow_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is { } item) ShowInFolder(item);
    }

    private async void MenuCopy_Click(object? sender, RoutedEventArgs e) => await CopyLinks(Selected());
    private async void MenuExport_Click(object? sender, RoutedEventArgs e) => await ExportLinks(Selected());

    private void MenuDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected() is [var item]) DetailSheet.Open(this, item);
    }

    private void MenuEdit_Click(object? sender, RoutedEventArgs e) => EditSheet.Open(this, Selected());
    private void MenuRemove_Click(object? sender, RoutedEventArgs e) => RemoveAsk(Selected());
    private void MenuExtract_Click(object? sender, RoutedEventArgs e) => _downloads.ExtractNow(Selected());
    private void MenuPassword_Click(object? sender, RoutedEventArgs e) => PasswordSheet.Open(this, Selected());

    private void MenuAfter_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen && Selected() is [var item]) AfterSheet.Open(this, item);
    }

    // ---------------------------------------------------------------- Aktionen

    private IEnumerable<object> SelectedObjects() => List.SelectedItems?.Cast<object>() ?? Enumerable.Empty<object>();

    /// <summary>Die markierten Downloads. Ein markierter Paketkopf steht für alle seine sichtbaren Teile.</summary>
    private List<DownloadItem> Selected() => PackageGrouping.Expand(SelectedObjects());

    private List<DownloadItem> SelectedRows() => SelectedObjects().OfType<DownloadItem>().ToList();

    private List<PackageHead> SelectedHeads() => SelectedObjects().OfType<PackageHead>().ToList();

    private void ToggleAll(List<DownloadItem> items)
    {
        // Wie ein Schalter: läuft irgendeiner, wird alles angehalten, sonst alles gestartet.
        if (items.Any(i => i.Status is DownloadStatus.Running or DownloadStatus.Queued)) _downloads.Pause(items);
        else _downloads.Start(items);
    }

    /// <summary>Fragt nach, wenn dabei schon geladene Teildaten verloren gehen.</summary>
    private async void RemoveAsk(List<DownloadItem> items)
    {
        if (items.Count == 0) return;
        int partial = items.Count(i => i.Status != DownloadStatus.Completed && i.DownloadedBytes > 0);
        if (partial > 0
            && !await ConfirmSheet.AskAsync(this, Loc.Plural("Main.Remove.Ask", items.Count),
                Loc.Plural("Main.Remove.Partial", partial), Loc.T("Menu.Remove")))
            return;
        _downloads.Remove(items);
    }

    private Task CopyLinks(List<DownloadItem> items) => CopyText(string.Join(Environment.NewLine, items.Select(i => i.Url)));

    /// <summary>Speichert die Links der markierten Downloads (die Hoster-Links, nie entsperrte) als Textdatei.</summary>
    private async Task ExportLinks(List<DownloadItem> items)
    {
        if (items.Count == 0) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Export.Title"),
                SuggestedFileName = LinkExport.SuggestedName(items),
                DefaultExtension = "txt",
                ShowOverwritePrompt = true,
                FileTypeChoices = Dialogs.FileTypes(Loc.T("Export.Filter")),
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_downloads.Settings.EffectiveDownloadFolder),
            });
            if (file?.TryGetLocalPath() is { } path) LinkExport.Write(path, items.Select(i => i.Url));
        }
        catch (Exception ex) // der Dialog des Systems kann auf viele Arten scheitern, ein async-void-Aufrufer darf nie abstürzen
        {
            await ConfirmSheet.TellAsync(this, Loc.T("Export.Failed"), ex.Message);
        }
    }

    private void OpenFile(DownloadItem item)
    {
        string path = item.ShownPath;
        if (File.Exists(path)) RunShell(shell => shell.Open(path));
    }

    /// <summary>Markiert die Datei im Dateimanager. Unfertig: die .part, sonst nur der Ordner.</summary>
    private void ShowInFolder(DownloadItem item)
    {
        string path = item.ShownPath;
        if (!File.Exists(path)) path = item.TargetPath + ".part";
        if (File.Exists(path)) RunShell(shell => shell.ShowInFolder(path));
        else if (Directory.Exists(item.Folder))
        {
            string folder = item.Folder;
            RunShell(shell => shell.Open(folder));
        }
    }

    /// <summary>Der Dateimanager antwortet erst nach Sekunden, deshalb nie auf dem UI-Thread.</summary>
    internal async void RunShell(Action<Platform.IShell> action)
    {
        try
        {
            var shell = _app.Services.Shell;
            await Task.Run(() => action(shell));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsSheetOpen) await ConfirmSheet.TellAsync(this, Loc.T("Shell.Failed"), ex.Message);
        }
    }

    /// <summary>Scheitert die Aktion nach dem Countdown, steht das im Fenster, nicht nur in einer Meldung.</summary>
    private async void OnAfterCompletionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Power.AfterCompletion.Error) || _downloads.AfterCompletion.Error is not { } error) return;
        Reveal();
        await ConfirmSheet.TellAsync(this, Loc.T("Main.ActionFailed"), error);
    }
}
