using System.Collections.Generic;
using D6Downloader.Platform.Linux;
using Xunit;

namespace D6Downloader.Tests;

public class LinuxUserLanguagesTests
{
    private static IReadOnlyList<string> Get(params (string Name, string Value)[] variables)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, value) in variables) map[name] = value;
        return new LinuxUserLanguages(name => map.GetValueOrDefault(name)).Get();
    }

    [Fact]
    public void Locale_priority_is_lc_all_then_lc_messages_then_lang()
    {
        Assert.Equal(new[] { "de-DE" }, Get(("LANG", "de_DE.UTF-8")));
        Assert.Equal(new[] { "fr-FR" }, Get(("LANG", "de_DE.UTF-8"), ("LC_MESSAGES", "fr_FR@euro")));
        Assert.Equal(new[] { "it-IT" }, Get(("LC_MESSAGES", "fr_FR"), ("LC_ALL", "it_IT.utf8")));
    }

    [Fact]
    public void Language_list_comes_first_but_not_for_the_c_locale()
    {
        Assert.Equal(new[] { "pt-BR", "en", "de-DE" }, Get(("LANGUAGE", "pt_BR:en"), ("LANG", "de_DE.UTF-8")));
        Assert.Empty(Get(("LANGUAGE", "de"), ("LANG", "C.UTF-8")));
        Assert.Empty(Get());
    }
}
