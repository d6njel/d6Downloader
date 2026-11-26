using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Downloads;
using D6Downloader.Packages;

namespace D6Downloader.Sheets;

/// <summary>
/// Passwort für ein Paket oder Archiv eingeben. Es gilt für den nächsten Versuch (wird zuerst probiert); mit dem Haken
/// „In Liste merken“ kommt es nach einem Erfolg in die Liste der Einstellungen.
/// </summary>
public partial class PasswordSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly List<DownloadItem> _items;

    public PasswordSheet() => throw new NotSupportedException();

    private PasswordSheet(MainWindow host, List<DownloadItem> items)
    {
        InitializeComponent();
        _host = host;
        _items = items;
        Subject.Text = items.Count == 1 ? SubjectOf(items[0]) : Loc.Plural("Password.Subjects", items.Count);
    }

    private static string SubjectOf(DownloadItem item) => string.IsNullOrEmpty(item.PackageName) ? item.FileName : item.PackageName;

    /// <summary>Öffnet das Sheet für die Downloads, die ein Passwort annehmen können. Ohne solche tut es nichts.</summary>
    public static void Open(MainWindow host, IEnumerable<DownloadItem> selected)
    {
        var items = selected.Where(host.Downloads.CanEnterPassword).ToList();
        if (items.Count == 0 || host.IsSheetOpen) return;
        var sheet = new PasswordSheet(host, items);
        host.ShowSheet(sheet, sheet.PasswordInput);
    }

    private void Password_Changed(object? sender, TextChangedEventArgs e) =>
        Ok.IsEnabled = PasswordRules.Clean(PasswordInput.Text) is not null;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        string password = PasswordInput.Text ?? "";
        bool remember = RememberBox.IsChecked == true;
        PasswordInput.Clear();
        _host.CloseSheet();
        _host.Downloads.EnterPassword(_items, password, remember);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        _host.CloseSheet();
    }
}
