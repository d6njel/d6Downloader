namespace D6Downloader.Downloads;

/// <summary>Ein Hinweis auf ein Datenproblem, den das Hauptfenster als Sheet zeigt.</summary>
public sealed record DataNotice(string Title, string Message);
