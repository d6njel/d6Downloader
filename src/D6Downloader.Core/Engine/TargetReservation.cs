using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace D6Downloader.Engine;

/// <summary>
/// Genau ein Schreiber je Zieldatei, auch über Prozesse hinweg (die App und <c>--headless-download</c>). Linux sperrt nicht von
/// selbst: <c>FileShare.Read</c> nimmt nur eine geteilte Sperre, zwei Downloads schrieben sonst in dieselbe <c>.part</c>.
/// Zwischen Prozessen dient ein Name im abstrakten Socket-Namensraum als Sperre: Er braucht keine Datei und wird vom Kern auch
/// nach einem Absturz sofort frei.
/// </summary>
internal sealed class TargetReservation : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> InProcess = new(StringComparer.Ordinal);

    private readonly string _key;
    private readonly Socket? _socket;

    private TargetReservation(string key, Socket? socket)
    {
        _key = key;
        _socket = socket;
    }

    /// <summary>Null, wenn schon jemand auf dieses Ziel schreibt. <paramref name="path"/> ist absolut.</summary>
    public static TargetReservation? TryAcquire(string path)
    {
        string key = Canonical(path);
        if (!InProcess.TryAdd(key, 0)) return null;

        Socket? socket = null;
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + key)));
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint("\0d6dl-target-" + hash));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket?.Dispose();
            InProcess.TryRemove(key, out _);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or PlatformNotSupportedException)
        {
            // Ohne diese Art Socket bleibt die Reservierung im Prozess.
            socket?.Dispose();
            socket = null;
        }
        return new TargetReservation(key, socket);
    }

    public void Dispose()
    {
        _socket?.Dispose();
        InProcess.TryRemove(_key, out _);
    }

    /// <summary>Verknüpfte Ordner aufgelöst, damit zwei Wege zum selben Ordner dieselbe Sperre treffen.</summary>
    private static string Canonical(string path)
    {
        string current = "/";
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (i == parts.Length - 1) break;
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return current;
    }
}
