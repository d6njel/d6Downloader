using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Debrid;
using D6Downloader.Downloads;

namespace D6Downloader.Sheets;

/// <summary>
/// AllDebrid: ein-/ausschalten, API-Key eintragen und prüfen, Liste der unterstützten Hoster. Erst „Übernehmen“ speichert.
/// Der Key wird nie angezeigt (verdecktes Feld, ein gespeicherter erscheint nicht im Feld): ein leeres Feld heißt „Key behalten“.
/// </summary>
public partial class DebridSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly CancellationTokenSource _closed = new();
    private bool _removeKey;
    private bool _busy;
    private bool _checkedOk; // „Prüfen“ war erfolgreich und der Nutzer hat den Schalter seither nicht angefasst
    // Zählen jede Änderung am Key und am Schalter. Ein Prüf-Ergebnis gilt nur, wenn sich während der Prüfung nichts geändert hat.
    private int _keyVersion, _switchVersion;
    private IReadOnlyList<string> _hosts = Array.Empty<string>();

    public DebridSheet() => throw new NotSupportedException();

    private DebridSheet(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _downloads = host.Downloads;
        EnabledBox.IsChecked = _downloads.Settings.Debrid.Enabled;
        _hosts = _downloads.Debrid.KnownHosts;
        DetachedFromVisualTree += (_, _) => _closed.Cancel(); // eine laufende Prüfung endet mit dem Sheet
        RefreshKeyState();
        RefreshHosts();
    }

    public static void Open(MainWindow host) => host.ShowSheet(new DebridSheet(host));

    private bool HasStoredKey => _downloads.Settings.Debrid.HasKey && !_removeKey;

    private void RefreshKeyState()
    {
        bool typing = Key.Length > 0;
        KeyState.Text = _removeKey
            ? Loc.T("Debrid.KeyWillBeRemoved")
            : typing ? Loc.T("Debrid.KeyNew")
            : HasStoredKey && !_downloads.Settings.Debrid.KeyUsable ? Loc.T("Debrid.KeyUnreadable")
            : HasStoredKey ? Loc.T("Debrid.KeyStored")
            : Loc.T("Debrid.KeyNone");
        RemoveKeyButton.IsVisible = _downloads.Settings.Debrid.HasKey && !_removeKey;
        CheckButton.IsEnabled = !_busy && (typing || HasStoredKey);
    }

    private string Key => KeyBox.Text ?? "";

    private string? _lastKey = "";

    private void KeyBox_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_downloads is null || Key == _lastKey) return; // Das Ereignis kommt verzögert, auch ohne echte Änderung.
        _lastKey = Key;
        if (Key.Length > 0) _removeKey = false;
        _checkedOk = false; // der geprüfte Key ist nicht mehr der im Feld
        _keyVersion++;
        CheckResult.IsVisible = false;
        RefreshKeyState();
    }

    private void RemoveKey_Click(object? sender, RoutedEventArgs e)
    {
        _removeKey = true;
        _checkedOk = false;
        _keyVersion++;
        _switchVersion++; // der Schalter geht unten aus
        KeyBox.Clear();
        _lastKey = "";
        EnabledBox.IsChecked = false;
        RefreshKeyState();
    }

    // ---------------------------------------------------------------- Prüfen

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        string? key = Key.Trim();
        if (key.Length > 0 && !DebridSettings.IsValidKey(key))
        {
            ShowResult(Loc.T("Debrid.KeyInvalid"), error: true);
            return;
        }
        if (key.Length == 0) key = _downloads.Settings.Debrid.ApiKey; // gespeicherten Key prüfen
        if (string.IsNullOrEmpty(key))
        {
            ShowResult(Loc.T("Debrid.KeyUnreadable"), error: true); // gespeichert, aber auf diesem Konto nicht lesbar
            return;
        }

        _busy = true;
        CheckButton.IsEnabled = false;
        ShowResult(Loc.T("Debrid.Checking"), error: false);
        int keyVersion = _keyVersion, switchVersion = _switchVersion;
        try
        {
            var account = await _downloads.Debrid.CheckAsync(key, _closed.Token);
            if (keyVersion != _keyVersion)
            {
                CheckResult.IsVisible = false; // inzwischen ein anderer Key (oder entfernt): Ergebnis gilt nicht mehr
                return;
            }
            // Hat der Nutzer den Schalter während der Prüfung angefasst, entscheidet er, nicht das Ergebnis.
            _checkedOk = account.IsPremium && switchVersion == _switchVersion;
            ShowResult(account.IsPremium && account.PremiumUntil is { } until
                ? Loc.T("Debrid.CheckOk", account.UserName,
                    until.LocalDateTime.ToString(Loc.T("Format.DatePattern"), System.Globalization.CultureInfo.InvariantCulture))
                : account.IsPremium ? Loc.T("Debrid.CheckOkOpen", account.UserName)
                : Loc.T("Debrid.CheckNoPremium", account.UserName), error: !account.IsPremium, success: account.IsPremium);
        }
        catch (OperationCanceledException)
        {
            return; // Sheet geschlossen
        }
        catch (Exception ex) // auch Unerwartetes nur anzeigen: Ein async-void-Handler darf die App nicht beenden
        {
            if (keyVersion == _keyVersion) ShowResult(Downloads.ErrorText.Describe(ex), error: true); // nicht ErrorText: so heißt hier das Textfeld
            else CheckResult.IsVisible = false;
        }
        finally
        {
            _busy = false;
            if (!_closed.IsCancellationRequested) RefreshKeyState();
        }
    }

    private void ShowResult(string text, bool error, bool success = false)
    {
        CheckResult.Text = text;
        CheckResult.Classes.Set("error", error);
        CheckResult.Classes.Set("success", success);
        CheckResult.IsVisible = true;
    }

    // ---------------------------------------------------------------- Hoster

    private async void LoadHosts_Click(object? sender, RoutedEventArgs e)
    {
        LoadHostsButton.IsEnabled = false;
        HostsState.Text = Loc.T("Debrid.HostsLoading");
        try
        {
            _hosts = await _downloads.Debrid.LoadHostsAsync(force: true, _closed.Token);
            RefreshHosts();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) // wie oben: anzeigen statt abstürzen
        {
            HostsState.Text = Loc.T("Debrid.HostsFailed", Downloads.ErrorText.Describe(ex));
        }
        finally
        {
            LoadHostsButton.IsEnabled = true;
        }
    }

    private void HostFilter_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_downloads is not null) RefreshHosts();
    }

    private void RefreshHosts()
    {
        LoadHostsButton.Content = Loc.T(_hosts.Count == 0 ? "Debrid.HostsLoad" : "Debrid.HostsReload");
        if (_hosts.Count == 0)
        {
            HostsState.Text = Loc.T("Debrid.HostsNotLoaded");
            HostList.ItemsSource = null;
            return;
        }
        string filter = (HostFilter.Text ?? "").Trim();
        var shown = filter.Length == 0
            ? _hosts
            : _hosts.Where(h => h.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        HostList.ItemsSource = shown;
        HostsState.Text = filter.Length == 0
            ? Loc.Plural("Debrid.HostsCount", _hosts.Count)
            : shown.Count == 0 ? Loc.T("Debrid.HostsNoMatch") : Loc.Plural("Debrid.HostsMatches", shown.Count, _hosts.Count);
    }

    // ---------------------------------------------------------------- Speichern

    private void Enabled_Click(object? sender, RoutedEventArgs e)
    {
        _checkedOk = false;
        _switchVersion++;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        bool enabled = EnabledBox.IsChecked == true || _checkedOk; // geprüfter, gültiger Key: einschalten
        string newKey = Key.Trim();
        if (newKey.Length > 0 && !DebridSettings.IsValidKey(newKey))
        {
            ErrorText.Text = Loc.T("Debrid.KeyInvalid");
            ErrorText.IsVisible = true;
            return;
        }
        if (enabled && newKey.Length == 0 && !HasStoredKey)
        {
            ErrorText.Text = Loc.T("Debrid.NeedKey");
            ErrorText.IsVisible = true;
            return;
        }
        if (enabled && newKey.Length == 0 && !_downloads.Settings.Debrid.KeyUsable)
        {
            ErrorText.Text = Loc.T("Debrid.KeyUnreadable"); // gespeichert, aber auf diesem Konto wertlos
            ErrorText.IsVisible = true;
            return;
        }
        _downloads.UpdateDebrid(enabled, newKey.Length > 0 ? newKey : null, _removeKey);
        KeyBox.Clear();
        _host.CloseSheet();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        KeyBox.Clear();
        _host.CloseSheet();
    }
}
