using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using D6Downloader.Platform;
using D6Downloader.Platform.Linux;

[assembly: SupportedOSPlatform("linux")]

namespace D6Downloader.Tests;

/// <summary>
/// Läuft vor allen Tests: eigenes HOME und eigene XDG-Ordner (nichts landet beim Benutzer), die lesenden Linux-Dienste, ein
/// Schlüssel nur im Speicher und deutsche Texte (sonst folgte die Sprache dem Rechner).
/// </summary>
internal static class TestSetup
{
    internal static readonly string Home = Path.Combine(Path.GetTempPath(), "d6dl-testhome-" + Guid.NewGuid().ToString("N"));

    private sealed class MemoryKey : ISecretKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] GetKey() => _key;
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        foreach (var (variable, folder) in new[]
        {
            ("HOME", ""), ("XDG_CONFIG_HOME", ".config"), ("XDG_DATA_HOME", ".local/share"),
            ("XDG_CACHE_HOME", ".cache"), ("XDG_RUNTIME_DIR", "run"),
        })
        {
            string path = Path.Combine(Home, folder);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(variable, path);
        }
        // Netz-Sperre: Alles außer dem eigenen Rechner läuft in einen toten Proxy, auch in Kindprozessen.
        foreach (string name in new[] { "http_proxy", "https_proxy", "HTTPS_PROXY", "ALL_PROXY", "all_proxy" })
            Environment.SetEnvironmentVariable(name, "http://127.0.0.1:1");
        Environment.SetEnvironmentVariable("no_proxy", "127.0.0.1,localhost");
        Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Home, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };

        LinuxPlatform.Register();
        PlatformServices.Trash = NoTrash.Instance; // nie der echte Papierkorb
        PlatformServices.SecretKey = new MemoryKey();
        Loc.Init("de");
    }
}
