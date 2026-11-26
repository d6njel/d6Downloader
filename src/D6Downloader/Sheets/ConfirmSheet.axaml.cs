using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace D6Downloader.Sheets;

/// <summary>Rückfrage oder Hinweis als Sheet statt eines eigenen Fensters.</summary>
public partial class ConfirmSheet : UserControl
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly MainWindow _host;

    public ConfirmSheet() => throw new NotSupportedException();

    private ConfirmSheet(MainWindow host, string heading, string message, string ok)
    {
        InitializeComponent();
        _host = host;
        Heading.Text = heading;
        Message.Text = message;
        Ok.Content = ok;
        DetachedFromVisualTree += (_, _) => _result.TrySetResult(false); // von einem anderen Sheet verdrängt
    }

    public static Task<bool> AskAsync(MainWindow host, string heading, string message, string ok)
    {
        var sheet = new ConfirmSheet(host, heading, message, ok);
        host.ShowSheet(sheet, sheet.Ok);
        return sheet._result.Task;
    }

    /// <summary>Nur ein Hinweis mit „OK“.</summary>
    public static Task TellAsync(MainWindow host, string heading, string message)
    {
        var sheet = new ConfirmSheet(host, heading, message, Loc.T("Common.OK"));
        sheet.Cancel.IsVisible = false;
        sheet.Ok.IsCancel = true;
        host.ShowSheet(sheet, sheet.Ok);
        return sheet._result.Task;
    }

    private void Close(bool result)
    {
        _result.TrySetResult(result);
        _host.CloseSheetIfShowing(this);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
