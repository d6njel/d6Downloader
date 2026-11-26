using System;
using System.Collections.Generic;
using D6Downloader.Packages;
using D6Downloader.Platform;

namespace D6Downloader.Platform.Linux;

/// <summary>7-Zip (<c>7zz</c>, <c>7z</c>, <c>7za</c>), <c>bsdtar</c>, <c>unar</c> und <c>lsar</c> aus dem PATH.</summary>
public sealed class LinuxToolLocator : IToolLocator
{
    public string? FindSevenZip(string? configured, IEnumerable<string> forbiddenDirs) =>
        ToolLocator.FindSevenZip(configured, ToolSearchEnv.Current(forbiddenDirs));

    public string? FindTar(IEnumerable<string> forbiddenDirs) => ToolLocator.FindTar(ToolSearchEnv.Current(forbiddenDirs));

    public string? FindUnar(IEnumerable<string> forbiddenDirs) => ToolLocator.FindUnar(ToolSearchEnv.Current(forbiddenDirs));

    public string? FindLsar(IEnumerable<string> forbiddenDirs) => ToolLocator.FindLsar(ToolSearchEnv.Current(forbiddenDirs));
}
