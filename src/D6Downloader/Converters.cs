using Avalonia;
using Avalonia.Data.Converters;
using D6Downloader.Downloads;
using D6Downloader.Sheets;

namespace D6Downloader;

/// <summary>Zustände aus dem Core in das, was die Vorlagen brauchen.</summary>
public static class Conv
{
    /// <summary>Wert der Ressource mit diesem Schlüssel (die Symbole der Seitenleiste).</summary>
    public static readonly IValueConverter Resource = new FuncValueConverter<string?, object?>(key =>
        key is { Length: > 0 } && Application.Current is { } app && app.TryGetResource(key, null, out var value) ? value : "");

    public static readonly IValueConverter IsPaused = Status(s => s == DownloadStatus.Paused);
    public static readonly IValueConverter IsQueued = Status(s => s == DownloadStatus.Queued);
    public static readonly IValueConverter IsFailed = Status(s => s == DownloadStatus.Failed);
    public static readonly IValueConverter IsCompleted = Status(s => s == DownloadStatus.Completed);
    public static readonly IValueConverter IsNotCompleted = Status(s => s != DownloadStatus.Completed);

    public static readonly IValueConverter ActionIcon = new FuncValueConverter<DownloadStatus, object?>(s =>
        Resource.Convert(s switch
        {
            DownloadStatus.Paused => "IconPlay",
            DownloadStatus.Failed => "IconRetry",
            DownloadStatus.Completed => "IconFolder",
            _ => "IconPause",
        }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));

    public static readonly IValueConverter ActionTip = new FuncValueConverter<DownloadStatus, string>(s => Loc.T(s switch
    {
        DownloadStatus.Paused => "Action.Start",
        DownloadStatus.Failed => "Action.Retry",
        DownloadStatus.Completed => "Action.ShowInFolder",
        _ => "Action.Pause",
    }));

    public static readonly IValueConverter Chevron = new FuncValueConverter<bool, object?>(open =>
        Resource.Convert(open ? "IconChevronDown" : "IconChevronRight", typeof(object), null,
            System.Globalization.CultureInfo.InvariantCulture));

    public static readonly IValueConverter Dotted = new FuncValueConverter<string?, string>(s =>
        string.IsNullOrEmpty(s) ? "" : " · " + s);

    public static readonly IValueConverter IsWarningTone = new FuncValueConverter<PackageTone, bool>(t => t == PackageTone.Warning);
    public static readonly IValueConverter IsErrorTone = new FuncValueConverter<PackageTone, bool>(t => t == PackageTone.Error);

    public static readonly IValueConverter IsWarning = new FuncValueConverter<Tone, bool>(t => t == Tone.Warning);
    public static readonly IValueConverter IsError = new FuncValueConverter<Tone, bool>(t => t == Tone.Error);

    public static readonly IValueConverter CheckShown = new FuncValueConverter<CheckStatus, bool>(c => c != CheckStatus.None);
    public static readonly IValueConverter CheckOnline = new FuncValueConverter<CheckStatus, bool>(c => c == CheckStatus.Online);
    public static readonly IValueConverter CheckOffline = new FuncValueConverter<CheckStatus, bool>(c => c == CheckStatus.Offline);
    public static readonly IValueConverter CheckWarn = new FuncValueConverter<CheckStatus, bool>(c =>
        c is CheckStatus.Protected or CheckStatus.NoAccess);

    private static IValueConverter Status(System.Func<DownloadStatus, bool> test) => new FuncValueConverter<DownloadStatus, bool>(test);
}
