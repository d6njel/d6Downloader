using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace D6Downloader;

/// <summary>Liest Links aus Drag &amp; Drop: Text und Links aus dem Browser, Textdateien und Internet-Verknüpfungen (.url).</summary>
public static class DropData
{
    private const long MaxFileSize = 4 * 1024 * 1024;

    public static bool CanRead(IDataTransfer data) => data.Contains(DataFormat.Text) || data.Contains(DataFormat.File);

    public static string? Read(IDataTransfer data)
    {
        var files = data.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        return files is { Count: > 0 } ? ReadFiles(files) : data.TryGetText();
    }

    /// <summary>Kleine Textdateien ganz, aus .url-Dateien nur die Zeile <c>URL=</c>.</summary>
    internal static string ReadFiles(IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        foreach (string file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileSize) continue;
                if (info.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string line in File.ReadLines(file))
                        if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) sb.AppendLine(line[4..]);
                }
                else
                {
                    sb.AppendLine(File.ReadAllText(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return sb.ToString();
    }
}
