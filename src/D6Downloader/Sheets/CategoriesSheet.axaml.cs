using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Downloads;

namespace D6Downloader.Sheets;

/// <summary>
/// Eine bearbeitbare Zeile. <see cref="OriginalKey"/> erkennt Umbenennungen, damit Downloads mitwandern;
/// <see cref="Id"/> hält die Kennung einer eingebauten Kategorie fest (sie behält sie auch umbenannt).
/// </summary>
public sealed class CategoryRow : Observable
{
    private string _name = "";
    private string _extensions = "";
    private string _folder = "";

    public string? OriginalKey { get; init; }
    public string? Id { get; init; }
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public string Extensions { get => _extensions; set => Set(ref _extensions, value ?? ""); }
    public string Folder { get => _folder; set => Set(ref _folder, value ?? ""); }
}

/// <summary>Kategorien anlegen, umbenennen, löschen. Erst „Übernehmen“ speichert.</summary>
public partial class CategoriesSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly ObservableCollection<CategoryRow> _rows = new();

    public CategoriesSheet() => throw new NotSupportedException();

    private CategoriesSheet(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        foreach (var c in host.Downloads.Settings.EffectiveCategories)
            _rows.Add(new CategoryRow { OriginalKey = c.Key, Id = c.Id, Name = c.Name, Extensions = c.Extensions, Folder = c.Folder ?? "" });
        Rows.ItemsSource = _rows;
    }

    public static void Open(MainWindow host) => host.ShowSheet(new CategoriesSheet(host));

    private void AddRow_Click(object? sender, RoutedEventArgs e) => _rows.Add(new CategoryRow { Name = Loc.T("Categories.NewName") });

    private void DeleteRow_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CategoryRow row) _rows.Remove(row);
    }

    private async void BrowseRow_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CategoryRow row) return;
        try
        {
            if (await Dialogs.PickFolderAsync(this, Loc.T("Categories.FolderFor", row.Name),
                    Directory.Exists(row.Folder) ? row.Folder : _host.Downloads.Settings.EffectiveDownloadFolder) is { } chosen)
                row.Folder = chosen;
        }
        catch (Exception)
        {
            ShowError(Loc.T("Add.BrowseFailed"));
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            string name = row.Name.Trim();
            if (name.Length == 0) { ShowError(Loc.T("Categories.NameRequired")); return; }
            if (!names.Add(name)) { ShowError(Loc.T("Categories.NameTwice", name)); return; }
            string folder = row.Folder.Trim();
            if (folder.Length > 0 && (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder)))
            {
                ShowError(Loc.T("Categories.FolderInvalid", name));
                return;
            }
        }

        var categories = _rows.Select(r => new CategorySetting
        {
            Id = r.Id,
            Name = r.Name.Trim(), // der Standardname einer eingebauten Kategorie bleibt ungespeichert und folgt der Sprache
            Extensions = r.Extensions.Trim(),
            Folder = r.Folder.Trim().Length > 0 ? r.Folder.Trim() : null,
        }).ToList();
        var renames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].OriginalKey is { } old && !string.Equals(old, categories[i].Key, StringComparison.Ordinal))
                renames[old] = categories[i].Key;

        _host.Downloads.UpdateCategories(categories, renames);
        _host.CloseSheet();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
