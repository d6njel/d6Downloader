using System;
using Avalonia.Markup.Xaml;

namespace D6Downloader;

/// <summary>
/// Text aus der Sprachdatei in XAML: <c>Text="{l:Str Settings.Title}"</c> mit <c>xmlns:l="clr-namespace:D6Downloader"</c>.
/// Der Text wird beim Laden der Oberfläche eingesetzt, die Sprache steht dann fest (siehe <see cref="Loc"/>).
/// </summary>
public sealed class StrExtension : MarkupExtension
{
    public StrExtension()
    {
    }

    public StrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
