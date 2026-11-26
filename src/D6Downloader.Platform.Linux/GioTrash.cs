using System;
using System.IO;
using D6Downloader.Platform;

namespace D6Downloader.Platform.Linux;

/// <summary>Papierkorb über <c>gio trash</c>. Geht das nicht (etwa ein Dateisystem ohne Papierkorb), bleibt die Datei liegen.</summary>
public sealed class GioTrash : ITrash
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ICommandRunner _commands;

    public GioTrash(ICommandRunner? commands = null) => _commands = commands ?? CommandRunner.Instance;

    public void MoveToTrash(string path)
    {
        string gio = _commands.Find("gio") ?? throw new IOException("gio fehlt.");
        var result = _commands.Run(gio, new[] { "trash", "--", path }, Timeout);
        if (result.ExitCode != 0) throw new IOException("gio trash ist gescheitert: " + path);
        if (File.Exists(path)) throw new IOException("Die Datei ist noch da: " + path);
    }
}
