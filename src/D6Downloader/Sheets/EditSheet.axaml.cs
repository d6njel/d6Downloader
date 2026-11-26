using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Downloads;

namespace D6Downloader.Sheets;

/// <summary>
/// Download bearbeiten: ein Eintrag mit Name, Ordner, Link, Kategorie und Prüfsumme, mehrere nur mit Ordner und Kategorie.
/// Solange ein Download läuft, bleiben Datei und Link gesperrt.
/// </summary>
public partial class EditSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly List<DownloadItem> _items;

    public EditSheet() => throw new NotSupportedException();

    private EditSheet(MainWindow host, List<DownloadItem> items)
    {
        InitializeComponent();
        _host = host;
        _downloads = host.Downloads;
        _items = items;
        var settings = _downloads.Settings;
        var first = items[0];

        if (items.Count == 1)
        {
            Heading.Text = first.FileName;
            string size = first.TotalBytes >= 0 ? " · " + Format.Bytes(first.TotalBytes) : "";
            Info.Text = Loc.T("Edit.Info", StatusName(first.Status), size, Format.When(first.AddedAt));
            NameBox.Text = first.FileName;
            FolderBox.Text = first.Folder;
            UrlBox.Text = first.Url;
            ChecksumBox.Text = first.Checksum ?? "";
            // Fertig: die Prüfsumme wirkt erst beim nächsten Laden, dann ist sie hier fehl am Platz.
            ChecksumPanel.IsVisible = first.Status != DownloadStatus.Completed;

            bool locked = first.Status == DownloadStatus.Running;
            foreach (var control in new Control[] { NameBox, FolderBox, BrowseButton, UrlBox, ChecksumBox }) control.IsEnabled = !locked;
            LockHint.IsVisible = locked;
            Choices.Fill(CategoryBox, CategoryChoices.Build(settings), first.Category);
        }
        else
        {
            Heading.Text = Loc.T("Edit.HeadingMany", items.Count);
            Info.Text = Loc.T("Edit.InfoMany");
            NamePanel.IsVisible = false;
            UrlPanel.IsVisible = false;
            ChecksumPanel.IsVisible = false;
            string folder = first.Folder;
            FolderBox.Text = items.All(i => i.Folder == folder) ? folder : "";
            FolderBox.PlaceholderText = Loc.T("Edit.Unchanged");
            if (items.Any(i => i.Status == DownloadStatus.Running))
            {
                LockHint.Text = Loc.T("Edit.LockHintMany");
                LockHint.IsVisible = true;
            }
            Choices.Fill(CategoryBox, CategoryChoices.Build(settings, keep: true), CategoryChoices.Keep);
        }
    }

    public static void Open(MainWindow host, List<DownloadItem> items)
    {
        if (items.Count == 0 || host.IsSheetOpen) return;
        var sheet = new EditSheet(host, items);
        host.ShowSheet(sheet, items.Count == 1 && sheet.NameBox.IsEnabled ? sheet.NameBox : sheet.FolderBox);
        if (items.Count == 1)
        {
            // Nur der Name ohne Endung ist markiert.
            string name = sheet.NameBox.Text ?? "";
            int dot = name.LastIndexOf('.');
            sheet.NameBox.SelectionStart = 0;
            sheet.NameBox.SelectionEnd = dot > 0 ? dot : name.Length;
        }
    }

    private static string StatusName(DownloadStatus status) => status switch
    {
        DownloadStatus.Running => Loc.T("Edit.Status.Running"),
        DownloadStatus.Queued => Loc.T("Edit.Status.Queued"),
        DownloadStatus.Completed => Loc.T("Edit.Status.Completed"),
        DownloadStatus.Failed => Loc.T("Edit.Status.Failed"),
        _ => Loc.T("Edit.Status.Paused"),
    };

    private void UrlBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_items is null) return;
        var item = _items[0];
        bool partial = item.Status != DownloadStatus.Completed && item.DownloadedBytes > 0;
        UrlHint.IsVisible = partial && (UrlBox.Text ?? "").Trim() != item.Url;
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        string current = (FolderBox.Text ?? "").Trim();
        try
        {
            if (await Dialogs.PickFolderAsync(this, Loc.T("Add.ChooseFolder"),
                    Directory.Exists(current) ? current : _items[0].Folder) is { } chosen)
                FolderBox.Text = chosen;
        }
        catch (Exception)
        {
            ShowError(Loc.T("Add.BrowseFailed"));
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        string folder = (FolderBox.Text ?? "").Trim();
        bool single = _items.Count == 1;
        var item = _items[0];
        bool editFiles = FolderBox.IsEnabled;

        // Erst alles prüfen, dann ändern.
        string? checksum = null;
        string url = (UrlBox.Text ?? "").Trim();
        string name = (NameBox.Text ?? "").Trim();
        if (editFiles && folder.Length > 0 && !IsValidFolder(folder)) { ShowError(Loc.T("Edit.Err.InvalidFolder")); return; }
        if (single && editFiles)
        {
            if (folder.Length == 0) { ShowError(Loc.T("Edit.Err.FolderRequired")); return; }
            if (name.Length == 0) { ShowError(Loc.T("Edit.Err.NameRequired")); return; }
            if (!IsHttpUrl(url)) { ShowError(Loc.T("Edit.Err.UrlScheme")); return; }
            string text = (ChecksumBox.Text ?? "").Trim();
            if (ChecksumPanel.IsVisible && text.Length > 0)
            {
                if (ChecksumText.Parse(text) is not { } spec)
                {
                    ShowError(Loc.T("Edit.Err.Checksum"));
                    return;
                }
                checksum = ChecksumText.ToText(spec);
            }
        }

        SaveButton.IsEnabled = false;
        string? error = null;
        if (single && editFiles)
        {
            _downloads.SetUrl(item, url);
            if (item.Status != DownloadStatus.Completed) _downloads.SetChecksum(item, checksum);
            error = await _downloads.RelocateAsync(item, folder, name);
        }
        else if (!single && folder.Length > 0)
        {
            foreach (var i in _items.Where(i => i.Status != DownloadStatus.Running))
                error ??= await _downloads.RelocateAsync(i, folder, i.FileName);
        }
        if (CategoryBox.Value() != CategoryChoices.Keep) _downloads.SetCategory(_items, CategoryBox.Value());

        if (error is not null)
        {
            SaveButton.IsEnabled = true;
            ShowError(error);
            return;
        }
        _host.CloseSheetIfShowing(this);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private static bool IsValidFolder(string folder) =>
        folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(folder);

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
