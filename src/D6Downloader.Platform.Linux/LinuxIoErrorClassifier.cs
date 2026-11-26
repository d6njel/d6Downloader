using System.IO;
using D6Downloader.Platform;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// .NET legt unter Linux bei Dateifehlern die errno in <c>HResult</c> ab (kleine positive Zahl). Eigene und Netzwerk-Fehler
/// haben dort einen negativen HRESULT. Manche errno werden zu eigenen Ausnahmetypen, die behandelt der Aufrufer.
/// </summary>
public sealed class LinuxIoErrorClassifier : IIoErrorClassifier
{
    // Werte aus asm-generic/errno (x86-64 und arm64).
    private const int Eperm = 1, Enoent = 2, Enxio = 6, Eacces = 13, Enodev = 19, Enotdir = 20, Erofs = 30,
        Enametoolong = 36, Enotconn = 107, Ehostdown = 112, Ehostunreach = 113, Estale = 116, Enomedium = 123;

    private const int HighestErrno = 4095;

    public IoErrorKind Classify(IOException ex) => ex.HResult switch
    {
        <= 0 or > HighestErrno => IoErrorKind.NotLocal,
        Enoent or Enxio or Enodev or Enotdir or Enotconn or Ehostdown or Ehostunreach or Estale or Enomedium => IoErrorKind.TargetGone,
        Enametoolong => IoErrorKind.PathTooLong,
        Eperm or Eacces or Erofs => IoErrorKind.NoAccess,
        _ => IoErrorKind.Other,
    };
}
