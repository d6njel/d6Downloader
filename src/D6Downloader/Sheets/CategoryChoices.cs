using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace D6Downloader.Sheets;

/// <summary>Ein Eintrag einer Auswahlliste: sichtbarer Text und gespeicherter Wert.</summary>
public sealed record Choice(string Label, string? Value)
{
    public override string ToString() => Label;
}

internal static class Choices
{
    public static void Fill(ComboBox box, IEnumerable<Choice> choices, string? value)
    {
        var list = choices.ToList();
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(c => c.Value == value) ?? list.FirstOrDefault();
    }

    public static string? Value(this ComboBox box) => (box.SelectedItem as Choice)?.Value;
}

/// <summary>Einträge für die Kategorie-Auswahl. Sonderwerte beginnen mit \u0001 und kollidieren nie mit einem Namen.</summary>
internal static class CategoryChoices
{
    public const string Auto = "\u0001auto";
    public const string Keep = "\u0001keep";

    public static List<Choice> Build(AppSettings settings, bool auto = false, bool keep = false)
    {
        var list = new List<Choice>();
        if (keep) list.Add(new(Loc.T("Category.Keep"), Keep));
        if (auto) list.Add(new(Loc.T("Category.Auto"), Auto));
        list.Add(new(Loc.T("Category.None"), null));
        foreach (var c in settings.EffectiveCategories) list.Add(new(c.Name, c.Key));
        return list;
    }
}
