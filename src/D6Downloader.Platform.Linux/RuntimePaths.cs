using System.IO;

namespace D6Downloader.Platform.Linux;

/// <summary>Socket und Sperrdatei der laufenden App.</summary>
public sealed record RuntimePaths(string Directory)
{
    public string Socket => Path.Combine(Directory, "ipc.sock");
    public string LockFile => Path.Combine(Directory, "instance.lock");

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR/d6downloader</c>. Ohne Laufzeitordner <c>~/.config/d6downloader/run</c>, nie ein Ordner in
    /// <c>/tmp</c>, den ein anderer Benutzer vorher anlegen könnte.
    /// </summary>
    public static RuntimePaths Resolve(XdgPaths? xdg = null)
    {
        xdg ??= new XdgPaths();
        return new(xdg.RuntimeDir is { } run ? Path.Combine(run, "d6downloader") : Path.Combine(xdg.AppConfigDir, "run"));
    }

    /// <summary>Legt den Ordner an (0700). Eine Verknüpfung an seiner Stelle wird abgelehnt.</summary>
    internal void Prepare()
    {
        if (new FileInfo(Directory).LinkTarget is not null) throw new IOException("Laufzeitordner ist eine Verknüpfung: " + Directory);
        Engine.PrivateDirectory.Ensure(Directory);
    }
}
