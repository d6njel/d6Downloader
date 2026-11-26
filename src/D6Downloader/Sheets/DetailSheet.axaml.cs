using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Downloads;

namespace D6Downloader.Sheets;

/// <summary>
/// Alle Angaben zu einem Download: Kopf, jede Verbindung mit eigenem Graphen und der Verlauf. Bei einem laufenden Download
/// aktualisiert sich alles im 4-Hz-Takt des <see cref="DownloadManager"/>, die Ansicht hat keinen eigenen Timer.
/// </summary>
public partial class DetailSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadDetail _detail;

    public DetailSheet() => throw new NotSupportedException();

    private DetailSheet(MainWindow host, DownloadItem item)
    {
        InitializeComponent();
        _host = host;
        _detail = new DownloadDetail(item);
        DataContext = _detail;
        DetachedFromVisualTree += (_, _) => _detail.Close();
    }

    public static void Open(MainWindow host, DownloadItem item)
    {
        if (host.IsSheetOpen) return;
        var sheet = new DetailSheet(host, item);
        host.ShowSheet(sheet, sheet.CloseButton);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
