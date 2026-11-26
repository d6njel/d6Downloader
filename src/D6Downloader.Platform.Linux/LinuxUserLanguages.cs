using System;
using System.Collections.Generic;
using D6Downloader.Platform;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Sprachen aus der Umgebung wie bei gettext: <c>LC_ALL</c>, sonst <c>LC_MESSAGES</c>, sonst <c>LANG</c> bestimmt die Locale;
/// <c>LANGUAGE</c> (durch <c>:</c> getrennt) hat Vorrang, aber nicht bei der Locale <c>C</c>.
/// </summary>
public sealed class LinuxUserLanguages : IUserLanguages
{
    private readonly Func<string, string?> _environment;

    public LinuxUserLanguages(Func<string, string?>? environment = null) =>
        _environment = environment ?? Environment.GetEnvironmentVariable;

    public IReadOnlyList<string> Get()
    {
        string locale = First("LC_ALL", "LC_MESSAGES", "LANG");
        string? main = ToTag(locale);
        var result = new List<string>();
        if (locale.Length > 0 && main is null) return result; // "C" oder "POSIX"
        foreach (string part in (_environment("LANGUAGE") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            Add(result, ToTag(part));
        Add(result, main);
        return result;
    }

    private string First(params string[] names)
    {
        foreach (string name in names)
            if (_environment(name) is { Length: > 0 } value) return value;
        return "";
    }

    /// <summary><c>pt_BR.UTF-8@euro</c> wird zu <c>pt-BR</c>. Null für <c>C</c>, <c>POSIX</c> und Leeres.</summary>
    private static string? ToTag(string locale)
    {
        int end = locale.IndexOfAny(new[] { '.', '@' });
        string tag = (end < 0 ? locale : locale[..end]).Replace('_', '-');
        return tag is "" or "C" or "POSIX" ? null : tag;
    }

    private static void Add(List<string> list, string? tag)
    {
        if (tag is not null && !list.Contains(tag)) list.Add(tag);
    }
}
