using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Downloads;
using D6Downloader.Packages;

namespace D6Downloader.Sheets;

/// <summary>Alle Einstellungen an einer Stelle. Erst „Übernehmen“ ändert und speichert sie.</summary>
public partial class SettingsSheet : UserControl
{
    private static readonly int[] Scales = { 80, 90, 100, 110, 125, 150, 175, 200 };

    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly bool _passwordsUnreadable;

    public SettingsSheet() => throw new NotSupportedException();

    private SettingsSheet(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _downloads = host.Downloads;
        var s = _downloads.Settings;

        FolderBox.Text = s.DownloadFolder ?? "";
        FolderBox.PlaceholderText = AppPaths.UserDownloads();
        Choices.Fill(ConcurrentBox, Enumerable.Range(1, 10).Select(n => new Choice(n.ToString(), n.ToString())),
            s.MaxConcurrent.ToString());
        Choices.Fill(ThemeBox, new Choice[]
        {
            new(Loc.T("Settings.System"), nameof(ThemeMode.System)),
            new(Loc.T("Settings.Theme.Light"), nameof(ThemeMode.Light)),
            new(Loc.T("Settings.Theme.Dark"), nameof(ThemeMode.Dark)),
        }, s.Theme.ToString());
        var scales = Scales.Contains(s.UiScale) ? Scales : Scales.Append(s.UiScale).Order().ToArray();
        Choices.Fill(ScaleBox, scales.Select(p => new Choice(Loc.T("Format.Percent", p), p.ToString())), s.UiScale.ToString());

        // Die Sprachen heißen in der eigenen Sprache. Leer = der Sprache des Systems folgen. Gilt nach dem Neustart.
        var languages = new List<Choice> { new(Loc.T("Settings.System"), "") };
        languages.AddRange(Loc.Languages.Select(l => new Choice(l.Name, l.Tag)));
        Choices.Fill(LanguageBox, languages, s.Language ?? "");

        Choices.Fill(CloseBox, new Choice[]
        {
            new(Loc.T("Settings.Close.Exit"), nameof(CloseAction.Exit)),
            new(Loc.T("Settings.Close.Tray"), nameof(CloseAction.Tray)),
        }, s.CloseAction.ToString());

        AutostartBox.IsChecked = _host.App.Services.Autostart.IsEnabled();
        ResumeBox.IsChecked = s.ResumeOnStart;
        KeepAwakeBox.IsChecked = s.KeepAwake;
        AutoExtractBox.IsChecked = s.AutoExtract;
        DeleteArchivesBox.IsChecked = s.DeleteArchivesAfterExtract;
        SevenZipBox.Text = s.SevenZipPath ?? "";
        UnarPasswordBox.IsChecked = s.AllowUnarPasswordOnCommandLine;
        ClickNLoadBox.IsChecked = s.ClickNLoad;
        RulesBox.Text = string.Join(Environment.NewLine, s.AutoDeselect);
        ClipboardBox.IsChecked = s.WatchClipboard;
        _passwordsUnreadable = s.ArchivePasswords.Unreadable;
        if (_passwordsUnreadable) // anderer Schlüssel oder beschädigt: nichts anzeigen, aber auch nicht still überschreiben
        {
            PasswordsNote.Text = Loc.T("Settings.ArchivePasswordsUnreadable");
            PasswordsNote.Classes.Add("warning");
        }
        else PasswordsBox.Text = string.Join(Environment.NewLine, s.ArchivePasswords.Passwords);
        // Nur wenn es etwas zu sagen gibt: der letzte Startversuch scheiterte (Port belegt).
        if (s.ClickNLoad && _host.App.ClickNLoadError is { } problem)
        {
            ClickNLoadStatus.Text = problem;
            ClickNLoadStatus.IsVisible = true;
        }
        PortableBox.IsChecked = AppPaths.IsPortable;
        DataDirText.Text = Loc.T("Settings.DataDir", AppPaths.DataDir);
        NotifyCompletedBox.IsChecked = s.NotifyCompleted;
        NotifyFailedBox.IsChecked = s.NotifyFailed;
        NotifyAllDoneBox.IsChecked = s.NotifyAllDone;
        AboutText.Text = AboutNotice();
    }

    /// <summary>Programmname mit Version, darunter der Herkunftshinweis aus der eingebetteten NOTICE.md.</summary>
    internal static string AboutNotice()
    {
        var assembly = typeof(SettingsSheet).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream("NOTICE.md")!);
        return $"d6Downloader {assembly.GetName().Version?.ToString(3)}\n{reader.ReadToEnd().Trim()}";
    }

    public static void Open(MainWindow host) => host.ShowSheet(new SettingsSheet(host));

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        string current = (FolderBox.Text ?? "").Trim();
        try
        {
            if (await Dialogs.PickFolderAsync(this, Loc.T("Settings.ChooseFolder"),
                    Directory.Exists(current) ? current : _downloads.Settings.EffectiveDownloadFolder) is { } chosen)
                FolderBox.Text = chosen;
        }
        catch (Exception)
        {
            ShowError(Loc.T("Add.BrowseFailed"));
        }
    }

    // Ein Klick übernimmt zuerst die offenen Änderungen (wie „Übernehmen“, das schließt dieses Sheet).
    // Scheitert das, bleibt das Sheet offen und zeigt die Meldung.
    private async void Rapidgator_Click(object? sender, RoutedEventArgs e) { if (await ApplyAsync()) RapidgatorSheet.Open(_host); }
    private async void Debrid_Click(object? sender, RoutedEventArgs e) { if (await ApplyAsync()) DebridSheet.Open(_host); }
    private async void Hosts_Click(object? sender, RoutedEventArgs e) { if (await ApplyAsync()) HostsSheet.Open(_host); }
    private async void Browser_Click(object? sender, RoutedEventArgs e) { if (await ApplyAsync()) BrowserSheet.Open(_host); }

    private async void Save_Click(object? sender, RoutedEventArgs e) => await ApplyAsync();

    private static bool IsFullPath(string path) =>
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path);

    // true = übernommen und das Sheet geschlossen.
    private async Task<bool> ApplyAsync()
    {
        ErrorText.IsVisible = false;
        string folder = (FolderBox.Text ?? "").Trim();
        if (folder.Length > 0 && !IsFullPath(folder))
        {
            ShowError(Loc.T("Settings.FolderInvalid"));
            return false;
        }
        string sevenZip = (SevenZipBox.Text ?? "").Trim().Trim('"');
        if (sevenZip.Length > 0 && !IsFullPath(sevenZip))
        {
            ShowError(Loc.T("Settings.SevenZipInvalid"));
            return false;
        }

        // Was fehlschlagen kann, zuerst: dann bleibt bei einem Fehler alles beim Alten.
        try
        {
            var autostart = _host.App.Services.Autostart;
            bool wanted = AutostartBox.IsChecked == true;
            // Bei gesetztem Haken auch ohne Änderung schreiben: Das korrigiert ein verschobenes Programm.
            if (wanted || autostart.IsEnabled()) autostart.Set(wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.T("Settings.AutostartFailed", ex.Message));
            return false;
        }

        // Der Datenordner zuerst, damit die neuen Einstellungen schon im neuen Ordner landen.
        bool portable = PortableBox.IsChecked == true;
        if (portable != AppPaths.IsPortable)
        {
            SaveButton.IsEnabled = false;
            if (await _downloads.MoveDataAsync(portable) is { } error)
            {
                SaveButton.IsEnabled = true;
                ShowError(error);
                return false;
            }
        }

        // Der Server auch zuerst: Ist der Port belegt, bleibt der Dialog offen und sagt es, statt still zu scheitern.
        bool clickNLoad = ClickNLoadBox.IsChecked == true;
        if (clickNLoad)
        {
            if (_host.App.StartClickNLoad() is { } problem)
            {
                ShowError(problem);
                SaveButton.IsEnabled = true;
                return false;
            }
        }
        else _host.App.StopClickNLoad();

        var s = _downloads.Settings;
        s.ClickNLoad = clickNLoad;
        s.AutoDeselect = DeselectRules.Normalize((RulesBox.Text ?? "").Split('\n'));
        s.WatchClipboard = ClipboardBox.IsChecked == true;
        // Eine nicht lesbare Liste bleibt, wie sie ist, solange nichts Neues eingetragen wird.
        if (!_passwordsUnreadable || (PasswordsBox.Text ?? "").Trim().Length > 0)
            s.ArchivePasswords.Passwords = PasswordRules.ParseLines(PasswordsBox.Text);
        s.DownloadFolder = folder.Length > 0 ? folder : null;
        s.MaxConcurrent = int.Parse(ConcurrentBox.Value()!);
        s.Theme = Enum.Parse<ThemeMode>(ThemeBox.Value()!);
        s.UiScale = int.Parse(ScaleBox.Value()!);
        s.Language = string.IsNullOrEmpty(LanguageBox.Value()) ? null : LanguageBox.Value();
        s.CloseAction = Enum.Parse<CloseAction>(CloseBox.Value()!);
        s.ResumeOnStart = ResumeBox.IsChecked == true;
        s.KeepAwake = KeepAwakeBox.IsChecked == true;
        s.AutoExtract = AutoExtractBox.IsChecked == true;
        s.DeleteArchivesAfterExtract = DeleteArchivesBox.IsChecked == true;
        s.AllowUnarPasswordOnCommandLine = UnarPasswordBox.IsChecked == true;
        s.SevenZipPath = sevenZip.Length > 0 ? sevenZip : null;
        s.NotifyCompleted = NotifyCompletedBox.IsChecked == true;
        s.NotifyFailed = NotifyFailedBox.IsChecked == true;
        s.NotifyAllDone = NotifyAllDoneBox.IsChecked == true;

        _host.App.ApplyAppearance();
        _host.App.UpdateClipboardWatch();

        // Erst schließen, wenn gespeichert ist: Ein Schreibfehler steht im Sheet.
        SaveButton.IsEnabled = false;
        string? saveError = await _downloads.SettingsChangedAsync();
        SaveButton.IsEnabled = true;
        if (saveError is not null)
        {
            ShowError(saveError);
            return false;
        }
        _host.CloseSheet();
        return true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
