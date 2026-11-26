using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Browser;

namespace D6Downloader.Sheets;

/// <summary>
/// Browser verbinden: schreibt das Manifest des Native-Messaging-Hosts in die Browser-Ordner (oder entfernt es) und zeigt den
/// Ordner der Erweiterung. Geändert wird nur auf Knopfdruck.
/// </summary>
public partial class BrowserSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly BrowserSetup _setup;
    private readonly string _extensionDir;

    public BrowserSheet() => throw new NotSupportedException();

    private BrowserSheet(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _setup = host.App.Services.Browser;
        _extensionDir = host.App.Services.ExtensionDir;
        FolderText.Text = _extensionDir;
        Refresh();
    }

    public static void Open(MainWindow host) => host.ShowSheet(new BrowserSheet(host));

    private void Refresh()
    {
        Show(BrowserKind.Chrome, ChromeState, ChromeButton);
        Show(BrowserKind.Firefox, FirefoxState, FirefoxButton);
    }

    private void Show(BrowserKind kind, TextBlock state, Button button)
    {
        // Ohne Profilordner gibt es nichts, wohin sich etwas schreiben ließe.
        bool present = _setup.ManifestPaths(kind).Count > 0;
        var current = _setup.GetState(kind);
        state.Text = Loc.T(!present ? "Browser.NotFound" : current switch
        {
            BrowserState.Registered => "Browser.StateOk",
            BrowserState.Outdated => "Browser.StateOutdated",
            _ => "Browser.StateNone",
        });
        state.Classes.Set("success", current == BrowserState.Registered);
        button.IsEnabled = present;
        button.Content = Loc.T(current switch
        {
            BrowserState.Registered => "Browser.Disconnect",
            BrowserState.Outdated => "Browser.Update",
            _ => "Browser.Connect",
        });
        button.Tag = current;
    }

    private void Chrome_Click(object? sender, RoutedEventArgs e) => Toggle(BrowserKind.Chrome, ChromeButton);
    private void Firefox_Click(object? sender, RoutedEventArgs e) => Toggle(BrowserKind.Firefox, FirefoxButton);

    /// <summary>Verbinden oder Aktualisieren, bei bestehender passender Verbindung trennen.</summary>
    private void Toggle(BrowserKind kind, Button button)
    {
        ErrorText.IsVisible = false;
        try
        {
            if (button.Tag is BrowserState.Registered) _setup.Unregister(kind);
            else _setup.Register(kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.T("Browser.Err", ex.Message));
        }
        Refresh();
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_extensionDir))
        {
            ShowError(Loc.T("Browser.NoFolder", _extensionDir));
            return;
        }
        string folder = _extensionDir;
        _host.RunShell(shell => shell.Open(folder));
    }

    private async void CopyPath_Click(object? sender, RoutedEventArgs e) => await _host.CopyText(_extensionDir);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
