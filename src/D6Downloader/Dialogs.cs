using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace D6Downloader;

/// <summary>Dateidialoge der Arbeitsumgebung (unter KDE über das Portal).</summary>
public static class Dialogs
{
    /// <summary>Ordner wählen. Null = abgebrochen. Wirft, wenn es keinen Dialog gibt.</summary>
    public static async Task<string?> PickFolderAsync(Control owner, string title, string? start)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return null;
        var options = new FolderPickerOpenOptions { Title = title };
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(start);
        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>Filter der Sprachdateien (<c>Name|*.txt|Name|*.*</c>) für den Dialog.</summary>
    internal static List<FilePickerFileType> FileTypes(string filter)
    {
        var result = new List<FilePickerFileType>();
        string[] parts = filter.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            int paren = parts[i].LastIndexOf(" (", StringComparison.Ordinal);
            result.Add(new FilePickerFileType(paren > 0 ? parts[i][..paren] : parts[i])
            {
                Patterns = new[] { parts[i + 1] == "*.*" ? "*" : parts[i + 1] },
            });
        }
        return result;
    }
}
