using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Die eingecheckten Symbole (siehe tools/render-icons.sh) haben die Größen, die ihr Platz erwartet.</summary>
public class AppIconTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "d6Downloader.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("d6Downloader.sln nicht gefunden.");
    }

    /// <summary>Breite und Höhe aus dem IHDR-Block, dem ersten Block jeder PNG-Datei.</summary>
    private static (int Width, int Height) PngSize(string relative)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Root, relative));
        Assert.True(data.Length >= 24, relative);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, data[..8]);
        Assert.Equal("IHDR"u8.ToArray(), data[12..16]);
        return (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("manifest.firefox.json")]
    public void Extension_icons_match_their_manifest_sizes(string manifest)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "extension", manifest)));
        var root = doc.RootElement;
        var icons = root.GetProperty("icons").EnumerateObject()
            .Concat(root.GetProperty("action").GetProperty("default_icon").EnumerateObject())
            .ToList();
        Assert.NotEmpty(icons);
        foreach (var icon in icons)
        {
            int size = int.Parse(icon.Name);
            Assert.Equal((size, size), PngSize(Path.Combine("extension", icon.Value.GetString()!)));
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void Hicolor_icons_have_the_size_of_their_folder(int size) =>
        Assert.Equal((size, size), PngSize($"assets/icons/hicolor/{size}x{size}/apps/d6downloader.png"));

    [Theory]
    [InlineData("assets/d6downloader-256.png", 256)]
    [InlineData("src/D6Downloader/Assets/icon.png", 256)]
    [InlineData("src/D6Downloader/Assets/tray.png", 64)]
    public void Program_icons_have_the_expected_size(string path, int size) => Assert.Equal((size, size), PngSize(path));

    [Theory]
    [InlineData("assets/d6downloader.svg")]
    [InlineData("assets/d6downloader-small.svg")]
    public void Icon_sources_use_shapes_only(string path)
    {
        string svg = File.ReadAllText(Path.Combine(Root, path));
        Assert.DoesNotContain("<text", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("font-family", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<image", svg, StringComparison.OrdinalIgnoreCase);
    }
}
