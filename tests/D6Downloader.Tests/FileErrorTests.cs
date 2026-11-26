using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using D6Downloader.Downloads;
using D6Downloader.Engine;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Lokale Dateifehler (Ordner weg, Pfad zu lang, Platte abgezogen) sind keine abgebrochene Verbindung.</summary>
public class FileErrorTests
{
    private static string Text(Exception ex) => ErrorText.Describe(ex);

    /// <summary>So legt .NET unter Linux eine errno ab.</summary>
    private static IOException Errno(int errno, string message = "x") => new(message, errno);

    [Fact]
    public void Missing_folder_and_drive_get_their_own_text()
    {
        string expected = Loc.T("Error.FolderNotFound");
        Assert.Equal(expected, Text(new StorageException(new DirectoryNotFoundException())));
        Assert.Equal(expected, Text(new StorageException(new DriveNotFoundException())));
        Assert.Equal(expected, Text(new DirectoryNotFoundException()));
    }

    [Theory]
    [InlineData(2)]   // ENOENT: Pfad nicht gefunden
    [InlineData(19)]  // ENODEV: Gerät weg (Platte abgezogen)
    [InlineData(107)] // ENOTCONN: eingehängtes Netzlaufwerk (FUSE) getrennt
    [InlineData(116)] // ESTALE: NFS-Freigabe weg
    [InlineData(112)] // EHOSTDOWN
    public void Errors_for_a_vanished_target_are_not_a_network_abort(int errno)
    {
        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(Errno(errno)));
        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(new StorageException(Errno(errno))));
    }

    [Fact]
    public void Path_too_long_gets_its_own_text()
    {
        string expected = Loc.T("Error.PathTooLong");
        Assert.Equal(expected, Text(new StorageException(new PathTooLongException())));
        Assert.Equal(expected, Text(new PathTooLongException()));
        Assert.Equal(expected, Text(Errno(36))); // ENAMETOOLONG
    }

    [Fact]
    public void Other_file_errors_show_the_storage_text_with_the_reason()
    {
        string text = Text(Errno(16, "Die Datei wird von einem anderen Prozess verwendet")); // EBUSY

        Assert.Equal(Loc.T("Error.Storage", "Die Datei wird von einem anderen Prozess verwendet"), text);
        Assert.NotEqual(Loc.T("Error.ConnectionAborted"), text);
    }

    [Fact]
    public void Real_network_aborts_stay_a_connection_error()
    {
        string expected = Loc.T("Error.ConnectionAborted");
        Assert.Equal(expected, Text(new IOException("Verbindung vorzeitig beendet.")));
        Assert.Equal(expected, Text(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
    }

    [Fact]
    public void Access_denied_is_reported_as_no_access()
    {
        string expected = Loc.T("Error.NoAccess");
        Assert.Equal(expected, Text(new UnauthorizedAccessException()));
        Assert.Equal(expected, Text(new StorageException(new UnauthorizedAccessException())));
    }

    [Fact]
    public void Disk_full_keeps_its_text()
    {
        var full = new StorageException("voll", NativeFile.ErrorDiskFull, 5L * 1024 * 1024, 1L * 1024 * 1024);
        Assert.Equal(Loc.T("Error.DiskFull", 5, 1), Text(full));
    }

    // ---------------------------------------------------------------- Engine

    [Fact]
    public async Task Folder_that_cannot_be_created_is_a_storage_error_not_a_connection_error()
    {
        using var dir = new TempDir();
        string blocker = dir.File("blocker");
        File.WriteAllText(blocker, "ich bin eine Datei, kein Ordner");
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", Path.Combine(blocker, "sub", "a.bin")), null, default));

        Assert.NotEqual(Loc.T("Error.ConnectionAborted"), Text(ex));
        Assert.False(HttpDownloader.IsRetryable(ex));
    }

    [Fact]
    public async Task Missing_drive_is_a_storage_error()
    {
        // Unter Linux gibt es keine Laufwerke. Der entsprechende Fall: Der Zielordner führt über einen Symlink auf eine
        // Platte, die nicht eingehängt ist.
        using var dir = new TempDir();
        string link = dir.File("platte");
        Directory.CreateSymbolicLink(link, dir.File("nicht-eingehaengt"));
        string drive = Path.Combine(link, "ydl-test", "a.bin");
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", drive), null, default));

        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(ex));
    }

    [Fact]
    public async Task Target_folder_that_is_a_dead_link_is_reported_as_not_found()
    {
        using var dir = new TempDir();
        string link = dir.File("platte");
        Directory.CreateSymbolicLink(link, dir.File("nicht-eingehaengt"));
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", Path.Combine(link, "a.bin")), null, default));

        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(ex));
    }

    [Fact]
    public async Task Overlong_name_part_is_a_storage_error()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, new string('a', 300), "a.bin");
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", path), null, default));

        Assert.Equal(Loc.T("Error.PathTooLong"), Text(ex));
    }

    /// <summary>Prüft die Annahme dahinter an einem echten Fehler: /dev/full meldet beim Schreiben immer ENOSPC.</summary>
    [Fact]
    public void Real_write_error_carries_the_errno_in_HResult()
    {
        var ex = Assert.Throws<IOException>(() =>
        {
            using var full = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
            full.Write(new byte[16]);
        });
        Assert.Equal(NativeFile.ErrorDiskFull, ex.HResult);
        Assert.Equal(Loc.T("Error.Storage", ex.Message), Text(ex));
    }
}
