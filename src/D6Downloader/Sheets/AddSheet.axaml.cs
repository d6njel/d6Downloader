using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using D6Downloader.Debrid;
using D6Downloader.Downloads;
using D6Downloader.Engine;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Packages;
using D6Downloader.Remote;

namespace D6Downloader.Sheets;

/// <summary>
/// Hinzufügen-Dialog: erkennt Links in beliebigem Text, fragt Name und Größe per Probe ab (höchstens vier zugleich),
/// markiert Duplikate und filtert. Erst „Hinzufügen“ legt Downloads an.
/// </summary>
public partial class AddSheet : UserControl
{
    private const int ProbeParallel = 4;
    private const int RecentFolderCount = 8;

    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly RangeCollection<object> _rows = new(); // Paket-Köpfe (PackageHeader) und Links (AddCandidate)
    private readonly Dictionary<string, PackageHeader> _headers = new(StringComparer.Ordinal);
    private readonly List<AddCandidate> _checkQueue = new();
    private readonly DispatcherTimer _checkDelay;
    private List<AddCandidate> _visible = new(); // die Links, die der Filter durchlässt
    private readonly Dictionary<string, AddCandidate> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _existing;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(ProbeParallel);
    private readonly DispatcherTimer _parseDelay;
    private readonly DispatcherTimer _folderDelay;
    private readonly DispatcherTimer _refreshDelay;
    private List<AddCandidate> _parsed = new();
    private LinkFilter _filter = LinkFilter.All;
    private readonly DeselectRules _rules;
    private int _textDuplicates;
    private long _freeSpace = -1;
    private bool _closed;
    private bool _hostLoadStarted;
    private bool _hostLoadDone;
    private bool _hostWaitOver; // die Wartezeit auf die Hoster-Liste ist vorbei: neue Zeilen werden sofort geprobt
    private readonly List<AddCandidate> _waitingForHosts = new();

    /// <summary>So lange werden Zeilen höchstens zurückgehalten, bis die Hoster-Liste da ist.</summary>
    private static readonly TimeSpan HostWait = TimeSpan.FromSeconds(4);

    /// <summary>Nur für den XAML-Lader.</summary>
    public AddSheet() => throw new NotSupportedException();

    private AddSheet(MainWindow host, DownloadManager downloads, string? category, string? queueId)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        _existing = new HashSet<string>(downloads.Items.Select(i => LinkParser.Normalize(i.Url)), StringComparer.Ordinal);
        Candidates.ItemsSource = _rows;

        _parseDelay = Delay(250, Parse);
        _folderDelay = Delay(300, UpdateFolder);
        _refreshDelay = Delay(100, ApplyFilter);
        _checkDelay = Delay(400, RunChecks);

        var settings = downloads.Settings;
        _rules = DeselectRules.Parse(settings.AutoDeselect);
        FolderBox.Text = settings.RecentFolders.FirstOrDefault(Directory.Exists) ?? settings.EffectiveDownloadFolder;
        StartBox.IsChecked = settings.StartImmediately;
        Choices.Fill(CategoryBox, CategoryChoices.Build(settings, auto: true),
            settings.FindCategory(category)?.Key ?? CategoryChoices.Auto);
        CategoryBox.SelectionChanged += (_, _) =>
        {
            foreach (var c in _known.Values) AssignCategory(c);
            UpdateFolder();
        };

        var queues = new List<Choice> { new(Loc.T("Queue.Default"), null) };
        queues.AddRange(settings.Queues.Select(q => new Choice(q.Name, q.Id)));
        Choices.Fill(QueueBox, queues, settings.FindQueue(queueId)?.Id);
        QueuePanel.IsVisible = settings.Queues.Count > 0;
        // Auch wenn ein anderes Sheet (Countdown) diesen verdrängt, endet alles Laufende.
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>Die erkannten Links in Anzeigereihenfolge (Paketköpfe und Links).</summary>
    internal IReadOnlyList<object> Rows => _rows;

    /// <summary>
    /// Bei aktivem Debrid ohne bekannte Hoster-Liste: im Hintergrund laden (der Dialog bleibt bedienbar, jeder Dialog versucht es
    /// erneut). Die Zeilen warten darauf höchstens <see cref="HostWait"/>, dann werden sie bewertet: Hoster-Links ohne Probe,
    /// alles andere (auch ohne Liste) geprobt. Kommt die Liste später, werden Hoster-Links nachträglich auf Debrid umgestellt.
    /// </summary>
    private async void LoadHostsForProbes()
    {
        if (_hostLoadStarted) return;
        _hostLoadStarted = true;
        var router = _downloads.Router;
        var load = router.EnsureHosts(); // scheitert nie
        await Task.WhenAny(load, Task.Delay(HostWait));
        if (_closed) return;
        _hostWaitOver = true;
        var waiting = _waitingForHosts.ToArray();
        _waitingForHosts.Clear();
        foreach (var c in waiting)
        {
            if (!UseService(c)) Probe(c);
        }
        Refresh();
        if (load.IsCompleted)
        {
            _hostLoadDone = true;
            return;
        }

        await load;
        _hostLoadDone = true;
        if (_closed) return;
        foreach (var c in _known.Values)
            if (!c.ViaDebrid) UseService(c);
        Refresh();
    }

    /// <summary>Übernimmt ein Dienst (Rapidgator oder Debrid) den Link, wird er nicht geprobt, sondern beim Dienst geprüft.</summary>
    private bool UseService(AddCandidate candidate)
    {
        if (_downloads.Router.ServiceFor(candidate.Url) is not { } service) return false;
        candidate.UseDebrid(service.Provider.Name, service.Provider.Id);
        AssignCategory(candidate);
        QueueCheck(candidate);
        return true;
    }

    /// <summary>Bei ausgeschaltetem Debrid mit Key die Hoster-Liste einmal laden, damit der Hinweis weiß, welche Links passen.</summary>
    private async void LoadDebridHosts()
    {
        var debrid = _downloads.Debrid;
        if (_downloads.Settings.Debrid is not { Enabled: false, HasKey: true } || debrid.KnownHosts.Count > 0) return;
        try
        {
            await debrid.LoadHostsAsync(false, _cts.Token);
            if (!_closed) Refresh();
        }
        catch (Exception)
        {
            // ohne Liste kein Hinweis, mehr nicht
        }
    }

    /// <param name="category">Vorauswahl, etwa die Kategorie, die gerade in der Seitenleiste gewählt ist.</param>
    /// <param name="queueId">Vorauswahl, etwa die Queue, die gerade in der Seitenleiste gewählt ist.</param>
    public static AddSheet Open(MainWindow host, string? text, string? category = null, string? queueId = null)
    {
        var sheet = new AddSheet(host, host.Downloads, category, queueId);
        if (!string.IsNullOrEmpty(text)) sheet.LinksBox.Text = text.Trim() + Environment.NewLine;
        sheet.Parse();
        sheet.UpdateFolder();
        sheet.LoadDebridHosts();
        host.ShowSheet(sheet, sheet.LinksBox);
        sheet.LinksBox.CaretIndex = sheet.LinksBox.Text?.Length ?? 0;
        return sheet;
    }

    /// <summary>Archivpasswörter, die mit den Links kamen (Click'n'Load). Sie gehen verschlüsselt an die Pakete und Archive.</summary>
    private List<string> _passwords = new();

    public void AddPasswords(IEnumerable<string>? passwords)
    {
        if (passwords is not null) _passwords = PasswordRules.Normalize(_passwords.Concat(passwords));
    }

    /// <summary>Weitere Links anhängen (Drag &amp; Drop oder Strg+V bei offenem Dialog).</summary>
    public void Append(string text)
    {
        LeaveAuto(); // Was jetzt dazukommt, startet nie von selbst.
        string current = LinksBox.Text ?? "";
        if (current.Length > 0 && !current.EndsWith('\n')) current += Environment.NewLine;
        LinksBox.Text = current + text.Trim() + Environment.NewLine;
        LinksBox.CaretIndex = LinksBox.Text.Length;
        Parse();
    }

    private DispatcherTimer Delay(int ms, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_closed) action();
        };
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    // ---------------------------------------------------------------- Links erkennen und prüfen

    private void Parse()
    {
        _parseDelay.Stop();
        var urls = LinkParser.Extract(LinksBox.Text ?? "", out _textDuplicates);
        var parsed = new List<AddCandidate>(urls.Count);
        var router = _downloads.Router;
        foreach (string url in urls)
        {
            string key = LinkParser.Normalize(url);
            if (!_known.TryGetValue(key, out var candidate))
            {
                candidate = new AddCandidate(url);
                if (_existing.Contains(key)) candidate.MarkDuplicate();
                candidate.PropertyChanged += Candidate_PropertyChanged;
                _known[key] = candidate;
                if (!UseService(candidate))
                {
                    AssignCategory(candidate);
                    if (!_hostWaitOver && HostsUnknown(url))
                    {
                        _waitingForHosts.Add(candidate); // Probe erst, wenn feststeht, ob ein Dienst den Hoster übernimmt
                        LoadHostsForProbes();
                    }
                    else Probe(candidate);
                }
            }
            parsed.Add(candidate);
        }
        _parsed = parsed;
        ApplyFilter();
    }

    /// <summary>Ein aktiver Dienst kennt seine Hoster-Liste noch nicht, könnte den Link also übernehmen.</summary>
    private bool HostsUnknown(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && _downloads.Router.Services.Any(s => s.IsActive && !s.HostsLoaded && s.Supports(uri) is null);

    private async void Probe(AddCandidate candidate)
    {
        try
        {
            await _slots.WaitAsync(_cts.Token);
            try
            {
                var prober = _downloads.Prober;
                var info = await Task.Run(() => prober.ProbeAsync(candidate.Url, _cts.Token), _cts.Token);
                candidate.Apply(info);
                if (candidate.FinalKey is { } final && _existing.Contains(final)) candidate.MarkDuplicate();
                AssignCategory(candidate); // der echte Name kann eine andere Endung haben
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Dialog geschlossen
        }
        catch (Exception ex)
        {
            if (!_closed) candidate.Fail(ErrorText.Describe(ex));
        }
    }

    private void Candidate_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Name (Filter), Größe und Häkchen ändern Zähler und Auswahl: gebündelt neu berechnen.
        if (!_closed) Restart(_refreshDelay);
    }

    /// <summary>Kategorie aus der Auswahl oder nach Dateiendung, dazu deren Ordner. Prüft danach den Dateikonflikt.</summary>
    private void AssignCategory(AddCandidate candidate)
    {
        var settings = _downloads.Settings;
        string? chosen = CategoryBox.Value();
        var category = chosen == CategoryChoices.Auto ? settings.CategoryFor(candidate.Name) : settings.FindCategory(chosen);
        string? folder = string.IsNullOrWhiteSpace(category?.Folder) ? null : category!.Folder;
        candidate.SetCategory(category?.Key, folder, settings.CategoryDisplayName(category?.Key));
        CheckExists(candidate);
    }

    /// <summary>Wohin der Link kommt: Ordner seines Pakets, sonst der Ordner seiner Kategorie, sonst der des Dialogs.</summary>
    private string EffectiveFolder(AddCandidate candidate) =>
        candidate.Package?.FolderPath ?? candidate.Folder ?? Folder;

    private void CheckExists(AddCandidate candidate)
    {
        string folder = EffectiveFolder(candidate);
        candidate.SetExists(IsValidFolder(folder) && File.Exists(Path.Combine(folder, candidate.Name)));
    }

    private string Folder => (FolderBox.Text ?? "").Trim();

    private bool Matches(AddCandidate c) => _filter.IsEmpty || _filter.Matches(c.FilterData);

    /// <summary>
    /// Baut die Zeilen neu: mehrteilige Archive werden zu Paketen mit Kopfzeile (Name, Ordner, Häkchen für alle Teile), der Rest
    /// bleibt einzeln. Pakete entstehen aus den Namen aller erkannten Links, der Filter blendet nur Zeilen aus. Der Neuaufbau
    /// läuft gebündelt nach jeder Änderung an einem Namen, denn erst die Probe oder der Online-Check liefert oft den echten Namen.
    /// </summary>
    private void ApplyFilter()
    {
        _refreshDelay.Stop();
        // Regeln „Automatisch abwählen“: wählen nur ab (nie löschen), jede Zeile lässt sich wieder anhaken. Name, Größe und Status
        // kommen oft erst nach der Probe oder dem Online-Check, deshalb bei jedem Neuaufbau.
        foreach (var c in _parsed) c.ApplyRules(_rules.Count > 0 && _rules.Matches(c.FilterData));
        var rows = new List<object>(_parsed.Count + 4);
        var visible = new List<AddCandidate>(_parsed.Count);
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in PackageDetector.Group(_parsed, c => c.Name))
        {
            if (!group.IsPackage)
            {
                var single = group.Items[0];
                single.Package = null;
                if (!Matches(single)) continue;
                rows.Add(single);
                visible.Add(single);
                continue;
            }

            string key = PackageHeader.KeyOf(group.Name!, group.Kind!.Value);
            live.Add(key);
            if (!_headers.TryGetValue(key, out var header))
            {
                header = new PackageHeader(group.Name!, group.Kind.Value, () => Folder);
                header.Changed += Header_Changed;
                _headers[key] = header;
            }
            var shown = group.Items.Where(Matches).ToList();
            foreach (var c in group.Items) c.Package = header;
            header.SetMembers(group.Items, shown, group.Missing);
            if (shown.Count == 0) continue;
            rows.Add(header);
            rows.AddRange(shown);
            visible.AddRange(shown);
        }
        // Ein Paket, das sich auflöst (Teil geändert oder entfernt), verliert seine Kopfzeile samt Eingaben.
        foreach (string gone in _headers.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _headers[gone].Changed -= Header_Changed;
            _headers.Remove(gone);
        }

        _visible = visible;
        if (!rows.SequenceEqual(_rows)) _rows.ReplaceAll(rows);
        foreach (var c in _parsed)
            if (c.Package is not null) CheckExists(c); // Ordner oder Name des Pakets kann sich geändert haben
        Refresh();
    }

    private void Header_Changed(object? sender, EventArgs e)
    {
        if (!_closed) Restart(_refreshDelay);
    }

    // ---------------------------------------------------------------- Online-Check

    /// <summary>
    /// Merkt einen Link für den Online-Check beim Debrid-Dienst vor. Es kommen nur Links hierher, die der Dienst entsperren würde
    /// (<see cref="AddCandidate.ViaDebrid"/>: Dienst an, Hoster in der geladenen Liste); alle anderen gehen nie an den Dienst und
    /// behalten den Status „nicht geprüft“. Die Links werden kurz gesammelt und gemeinsam gefragt.
    /// </summary>
    private void QueueCheck(AddCandidate candidate)
    {
        if (!candidate.ViaDebrid || candidate.Check != CheckStatus.None || _checkQueue.Contains(candidate)) return;
        _checkQueue.Add(candidate);
        Restart(_checkDelay);
    }

    private async void RunChecks()
    {
        _checkDelay.Stop();
        var batch = _checkQueue.Where(c => c.ViaDebrid && c.Check == CheckStatus.None).ToList();
        _checkQueue.Clear();
        if (batch.Count == 0) return;

        var byUrl = batch.ToDictionary(c => c.Url, StringComparer.Ordinal);
        foreach (var c in batch) c.MarkChecking();
        Refresh();
        var router = _downloads.Router;
        try
        {
            await Task.Run(() => router.CheckLinksAsync(byUrl.Keys.ToList(), infos => Dispatcher.UIThread.Post(() =>
            {
                if (_closed) return;
                foreach (var info in infos)
                {
                    if (!byUrl.TryGetValue(info.Link, out var c)) continue;
                    c.ApplyCheck(info);
                    AssignCategory(c); // der Name kann eine andere Endung haben
                }
            }), _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Dialog geschlossen
        }
        catch (Exception)
        {
            // Dienst nicht erreichbar, Key falsch o. Ä.: Die Links bleiben „nicht geprüft“ und lassen sich trotzdem laden.
        }
        if (_closed) return;
        foreach (var c in batch) c.CheckUnknown();
        Refresh();
    }

    /// <summary>Zähler, Summe, freier Platz und Knopf-Zustand.</summary>
    private void Refresh()
    {
        int selected = 0, probing = 0;
        long bytes = 0;
        bool unknownSize = false;
        bool packagesOk = true;
        foreach (var c in _visible)
        {
            if (!c.IsChecked) continue;
            selected++;
            if (c.Size >= 0) bytes += c.Size;
            else unknownSize = true;
            if (c.Package is { } p && !IsValidFolder(p.FolderPath)) packagesOk = false;
        }
        int checking = 0, byRules = 0;
        foreach (var c in _parsed)
        {
            if (c.State == CandidateState.Probing) probing++;
            if (c.Check == CheckStatus.Checking) checking++;
            if (c.RuleDeselected) byRules++;
        }

        string count = _filter.IsEmpty ? Links(_parsed.Count) : Loc.T("Add.CountOf", _visible.Count, Links(_parsed.Count));
        int packages = _headers.Count;
        if (packages > 0) count += " · " + Loc.Plural("Add.Packages", packages);
        if (_textDuplicates > 0) count += " · " + Loc.Plural("Add.Duplicates", _textDuplicates);
        CountText.Text = _parsed.Count == 0 ? "" : count;

        EmptyText.IsVisible = _visible.Count == 0;
        EmptyText.Text = _parsed.Count == 0 ? Loc.T("Add.Empty") : Loc.T("Add.NoMatch");
        AllBox.IsEnabled = _visible.Count > 0;
        AllBox.IsChecked = _visible.Count == 0 || selected == 0 ? false : selected == _visible.Count ? true : null;

        bool folderOk = IsValidFolder(Folder);
        AddButton.IsEnabled = selected > 0 && folderOk && packagesOk;
        AddButton.Content = selected <= 1 ? Loc.T("Common.Add") : Loc.T("Add.AddN", selected);

        var parts = new List<string>(4);
        if (!folderOk) parts.Add(Loc.T("Add.InvalidFolder"));
        else if (!packagesOk) parts.Add(Loc.T("Add.InvalidPackageFolder"));
        else if (selected == 0) parts.Add(Loc.T("Add.NothingSelected"));
        else parts.Add(Loc.T(unknownSize ? "Add.SelectedMore" : "Add.Selected", selected, Format.Bytes(bytes)));
        if (probing > 0) parts.Add(Loc.T("Add.Probing", probing));
        if (checking > 0) parts.Add(Loc.T("Add.Checking", checking));
        if (byRules > 0) parts.Add(Loc.Plural("Add.RuleDeselected", byRules));
        if (folderOk && _freeSpace >= 0) parts.Add(Loc.T("Add.Free", Format.Bytes(_freeSpace)));
        SummaryText.Text = string.Join(" · ", parts);

        int debrid = _downloads.Debrid.CountSupportedWhileOff(_visible.Where(c => c.IsChecked).Select(c => c.Url));
        DebridHint.Text = debrid > 0 ? Loc.Plural("Add.DebridHint", debrid) : "";
        DebridHint.IsVisible = debrid > 0;

        bool tight = folderOk && _freeSpace >= 0 && bytes > _freeSpace;
        SummaryText.Classes.Set("error", !folderOk || !packagesOk);
        SummaryText.Classes.Set("warning", folderOk && packagesOk && tight);
    }

    private static string Links(int n) => Loc.Plural("Add.Links", n);

    private static bool IsValidFolder(string folder) =>
        folder.Length > 0 && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(folder);

    /// <summary>Freier Platz des Zielordners (oder seines nächsten vorhandenen Elternordners), Dateikonflikte neu prüfen.</summary>
    private void UpdateFolder()
    {
        _folderDelay.Stop();
        string folder = Folder;
        _freeSpace = -1;
        if (IsValidFolder(folder))
        {
            string? dir = folder;
            while (dir is not null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (dir is not null) _freeSpace = NativeFile.FreeSpace(dir);
        }
        foreach (var h in _headers.Values) h.Refresh(); // ihr automatischer Ordner hängt am Zielordner
        foreach (var c in _known.Values) CheckExists(c);
        Refresh();
    }

    // ---------------------------------------------------------------- Eingaben

    private void LinksBox_TextChanged(object? sender, TextChangedEventArgs e) => Restart(_parseDelay);

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = LinkFilter.Parse(FilterBox.Text);
        ApplyFilter();
    }

    private void FolderBox_TextChanged(object? sender, TextChangedEventArgs e) => Restart(_folderDelay);

    private void AllBox_Click(object? sender, RoutedEventArgs e)
    {
        // Bei Teilauswahl wählt der Klick alles an (sonst ergäbe der Dreizustand-Zyklus „alle abwählen“).
        bool value = !_visible.All(c => c.IsChecked);
        foreach (var c in _visible) c.IsChecked = value;
        Refresh();
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e) => await PasteClipboard();

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        e.Handled = true;
        await PasteClipboard();
    }

    private async Task PasteClipboard()
    {
        string? text = await _host.ClipboardText();
        if (!_closed && LinkParser.ContainsLink(text)) Append(text!);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        string current = Folder;
        try
        {
            if (await Dialogs.PickFolderAsync(this, Loc.T("Add.ChooseFolder"),
                    Directory.Exists(current) ? current : _downloads.Settings.EffectiveDownloadFolder) is { } chosen)
            {
                FolderBox.Text = chosen;
                UpdateFolder();
            }
        }
        catch (Exception)
        {
            SummaryText.Text = Loc.T("Add.BrowseFailed");
        }
    }

    // ---------------------------------------------------------------- Vom Handy: sofort starten

    private Action<RemoteOutcome>? _autoDone;
    private DispatcherTimer? _autoTimer;
    private TargetGuard? _guard;
    private IReadOnlySet<string> _unresolved = new HashSet<string>();
    private DateTime _autoDeadline;
    private bool _deciding;

    /// <summary>Läuft gerade die automatische Prüfung? Jede Eingabe des Benutzers beendet sie.</summary>
    internal bool IsAuto => _autoDone is not null;

    /// <summary>
    /// Öffnet den Dialog, ohne das Fenster zu zeigen, wartet auf Probe und Online-Check und startet dann die unbedenklichen
    /// Links. Prüfungen, Kategorien, Pakete, Duplikate und Regeln sind dieselben wie beim Dialog. Was unsicher ist, bleibt im
    /// Dialog stehen. <paramref name="done"/> wird genau einmal gerufen.
    /// </summary>
    /// <param name="unresolved">Links, deren Ziel sich nicht prüfen ließ: nur über den Dialog.</param>
    internal static AddSheet OpenAuto(MainWindow host, string text, TargetGuard guard, IReadOnlySet<string> unresolved,
        TimeSpan timeout, Action<RemoteOutcome> done)
    {
        var sheet = new AddSheet(host, host.Downloads, null, null)
        {
            _autoDone = done,
            _guard = guard,
            _unresolved = unresolved,
            _autoDeadline = DateTime.UtcNow + timeout,
        };
        sheet.FolderBox.Text = host.Downloads.Settings.EffectiveDownloadFolder;
        sheet.LinksBox.Text = text.Trim() + Environment.NewLine;
        sheet.Parse();
        sheet.UpdateFolder();
        sheet.AddHandler(PointerPressedEvent, (_, _) => sheet.LeaveAuto(), RoutingStrategies.Tunnel, handledEventsToo: true);
        sheet.AddHandler(KeyDownEvent, (_, _) => sheet.LeaveAuto(), RoutingStrategies.Tunnel, handledEventsToo: true);
        host.ShowSheet(sheet, sheet.LinksBox);
        sheet._autoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        sheet._autoTimer.Tick += (_, _) => sheet.AutoTick();
        sheet._autoTimer.Start();
        return sheet;
    }

    /// <summary>Ab jetzt ein gewöhnlicher Dialog: Der Benutzer entscheidet.</summary>
    private void LeaveAuto()
    {
        if (_autoDone is not { } done) return;
        _autoDone = null;
        _autoTimer?.Stop();
        done(new RemoteOutcome(ReceiptState.PendingConfirmation, _parsed.Count));
    }

    /// <summary>Ein anderes Sheet hat diesen Dialog verdrängt: Die Links vormerken, damit sie nicht verloren gehen.</summary>
    private void Displaced()
    {
        var done = _autoDone!;
        _autoDone = null;
        _autoTimer?.Stop();
        string text = LinksBox.Text ?? "";
        int count = _parsed.Count;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_host.App.Quitting) _host.ReceiveLinks(text);
            done(new RemoteOutcome(ReceiptState.PendingConfirmation, count));
        });
    }

    private bool Settled =>
        !_parseDelay.IsEnabled && !_refreshDelay.IsEnabled && !_checkDelay.IsEnabled && _checkQueue.Count == 0
        && _waitingForHosts.Count == 0 && (!_hostLoadStarted || _hostLoadDone)
        && _parsed.All(c => c.State != CandidateState.Probing && c.Check != CheckStatus.Checking);

    private async void AutoTick()
    {
        if (_autoDone is null || _deciding) return;
        if (!ReferenceEquals(_host.CurrentSheet, this))
        {
            Stop();
            return;
        }
        bool timedOut = DateTime.UtcNow >= _autoDeadline;
        if (!timedOut && !Settled) return;
        _deciding = true;
        try
        {
            await Decide(timedOut);
        }
        catch (Exception)
        {
            // Im Zweifel nichts starten: Der Dialog bleibt mit den Links stehen.
            LeaveAuto();
            _host.Reveal();
        }
        finally
        {
            _deciding = false;
        }
    }

    private async Task Decide(bool timedOut)
    {
        Parse(); // Pakete und Regeln auf dem letzten Stand
        var blocked = new HashSet<AddCandidate>();
        foreach (var c in _parsed.ToList())
        {
            // Nach Weiterleitungen: Auch das Ziel darf nicht im eigenen Netz liegen.
            if (c.FinalKey is { } final && final != c.Key && await _guard!.CheckAsync(final, _cts.Token) != TargetVerdict.Allowed)
                blocked.Add(c);
        }
        if (_autoDone is null || _closed) return; // inzwischen hat der Benutzer übernommen

        var ready = new List<AddCandidate>();
        var open = new List<AddCandidate>();
        int duplicates = 0, filtered = 0;
        foreach (var c in _parsed)
        {
            if (blocked.Contains(c)) continue;
            if (c.IsDuplicate || c.FileExists) duplicates++;
            else if (c.RuleDeselected) filtered++;
            else if (timedOut || !SafeToStart(c)) open.Add(c);
            else ready.Add(c);
        }
        // Ein Paket startet ganz oder gar nicht: Sonst wäre das Archiv auf zwei Pakete verteilt.
        var held = open.Concat(blocked).Select(c => c.Package).Where(p => p is not null).ToHashSet();
        foreach (var c in ready.Where(c => c.Package is not null && held.Contains(c.Package)).ToList())
        {
            ready.Remove(c);
            open.Add(c);
        }
        var rest = _parsed.Where(open.Contains).ToList(); // in der alten Reihenfolge
        int skipped = duplicates + filtered + blocked.Count;
        if (ready.Count > 0) Commit(ready, start: true);

        var done = _autoDone;
        _autoDone = null;
        _autoTimer?.Stop();
        if (rest.Count == 0)
        {
            Close();
            done(ready.Count > 0
                ? new RemoteOutcome(ReceiptState.Queued, ready.Count, skipped)
                : new RemoteOutcome(ReceiptState.Rejected, 0, skipped, blocked.Count > 0 ? ReceiptError.BlockedTarget
                    : filtered > 0 ? ReceiptError.Filtered : ReceiptError.AllDuplicate));
            return;
        }
        LinksBox.Text = string.Join(Environment.NewLine, rest.Select(c => c.Url)) + Environment.NewLine;
        Parse();
        _host.Reveal();
        done(ready.Count > 0
            ? new RemoteOutcome(ReceiptState.Partial, ready.Count, skipped)
            : new RemoteOutcome(ReceiptState.PendingConfirmation, rest.Count, skipped));
    }

    private bool SafeToStart(AddCandidate c)
    {
        if (c.State != CandidateState.Ready || _unresolved.Contains(c.Url)) return false;
        if (c.Check is CheckStatus.Offline or CheckStatus.Protected or CheckStatus.NoAccess or CheckStatus.Checking) return false;
        if (c.ServiceId is { } service)
        {
            if (_downloads.Router.Find(service)?.IsActive != true) return false;
        }
        else if (HostsUnknown(c.Url)) return false;
        // Rapidgator nur über das angemeldete Konto: Ohne wäre es die Webseite statt der Datei.
        if (RapidgatorLinks.IsRapidgator(c.Url)
            && (c.ServiceId != RapidgatorProvider.ProviderId || _downloads.Rapidgator.State != RapidgatorState.LoggedIn))
            return false;
        return CanWrite(EffectiveFolder(c));
    }

    /// <summary>Lässt sich im Ordner (oder in seinem nächsten vorhandenen Elternordner) eine Datei anlegen?</summary>
    private static bool CanWrite(string folder)
    {
        if (!IsValidFolder(folder)) return false;
        string? dir = folder;
        while (dir is not null && !Directory.Exists(dir))
        {
            if (File.Exists(dir)) return false;
            dir = Path.GetDirectoryName(dir);
        }
        if (dir is null) return false;
        try
        {
            using var probe = new FileStream(Path.Combine(dir, ".d6-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Abschluss

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        LeaveAuto();
        Parse(); // falls der Text eben erst geändert wurde
        string folder = Folder;
        var chosen = _visible.Where(c => c.IsChecked).ToList();
        if (chosen.Count == 0 || !IsValidFolder(folder) || chosen.Any(c => c.Package is { } p && !IsValidFolder(p.FolderPath))) return;

        bool start = StartBox.IsChecked == true;
        var settings = _downloads.Settings;
        settings.StartImmediately = start;
        settings.RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        settings.RecentFolders.Insert(0, folder);
        if (settings.RecentFolders.Count > RecentFolderCount)
            settings.RecentFolders.RemoveRange(RecentFolderCount, settings.RecentFolders.Count - RecentFolderCount);
        _downloads.SaveSettings();

        Commit(chosen, start);
        Close();
    }

    /// <summary>Legt die Downloads an: derselbe Weg für den Klick und für „sofort starten“.</summary>
    private void Commit(IReadOnlyList<AddCandidate> chosen, bool start)
    {
        string? protectedPasswords = PasswordRules.Encode(_passwords); // einmal verschlüsseln, nur für Pakete und Archive
        _downloads.Add(chosen.Select(c => new DownloadItem(c.Url, c.Name, EffectiveFolder(c))
        {
            TotalBytes = c.Size,
            Category = c.Category,
            QueueId = QueueBox.Value(),
            ViaService = c.ServiceId,
            PackageId = c.Package?.Id,
            PackageName = c.Package?.EffectiveName,
            ArchivePasswords = c.Package is not null || ExtractPlanner.IsSingleArchive(c.Name) ? protectedPasswords : null,
        }), start);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        LeaveAuto();
        Close();
    }

    private void Stop()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _parseDelay.Stop();
        _folderDelay.Stop();
        _refreshDelay.Stop();
        _checkDelay.Stop();
        _autoTimer?.Stop();
        if (_autoDone is not null) Displaced();
        foreach (var header in _headers.Values) header.Changed -= Header_Changed;
        foreach (var candidate in _known.Values) candidate.PropertyChanged -= Candidate_PropertyChanged;
    }

    private void Close()
    {
        Stop();
        _host.CloseSheet();
    }
}
