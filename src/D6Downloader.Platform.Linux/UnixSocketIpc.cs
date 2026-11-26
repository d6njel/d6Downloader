using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using D6Downloader.Browser;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Nachrichten zwischen den Prozessen, Rahmen wie beim Native Messaging (4 Byte Länge, UTF-8): <c>show</c> oder
/// <c>links:&lt;Text&gt;</c>. Die App antwortet mit <c>ok</c>. Der Socket ist nur für den eigenen Benutzer offen.
/// </summary>
internal static class IpcProtocol
{
    public const string Show = "show";
    public const string Links = "links:";
    public const string Ok = "ok";
    public const int MaxMessage = NativeMessage.MaxIncoming + 64;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
}

/// <summary>Die Seite der App. Nur wer die <see cref="SingleInstanceLock"/> hält, darf einen alten Socket ersetzen.</summary>
public sealed class IpcServer : IDisposable
{
    private const int MaxConnections = 8;

    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);

    private IpcServer(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    /// <summary>Ein zweiter Start bittet, das Fenster zu zeigen. Kommt auf einem Thread-Pool-Thread.</summary>
    public event Action? ShowRequested;

    /// <summary>Text mit Links (nie leer). Kommt auf einem Thread-Pool-Thread.</summary>
    public event Action<string>? LinksReceived;

    /// <summary>
    /// Öffnet den Socket (0600). Die Ereignisse vor <see cref="Listen"/> abonnieren, damit nichts verloren geht.
    /// </summary>
    public static IpcServer Create(SingleInstanceLock owner)
    {
        string path = owner.Paths.Socket;
        owner.Paths.Prepare();
        File.Delete(path); // Rest eines Absturzes: Die Sperre beweist, dass niemand mehr zuhört.
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new IpcServer(socket, path);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            socket.Dispose();
            throw new IOException("Socket nicht zu öffnen: " + ex.Message, ex);
        }
    }

    public void Listen()
    {
        _listener.Listen(MaxConnections);
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener.Dispose();
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (!_slots.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(() =>
            {
                try { Handle(client); }
                finally { _slots.Release(); }
            });
        }
    }

    private void Handle(Socket client)
    {
        try
        {
            client.ReceiveTimeout = client.SendTimeout = (int)IpcProtocol.Timeout.TotalMilliseconds;
            using var stream = new NetworkStream(client, ownsSocket: true);
            if (NativeMessage.Read(stream, IpcProtocol.MaxMessage) is not { } frame) return;
            string message = Encoding.UTF8.GetString(frame);
            Action? deliver;
            if (message == IpcProtocol.Show) deliver = () => ShowRequested?.Invoke();
            else if (message.StartsWith(IpcProtocol.Links, StringComparison.Ordinal))
            {
                string text = message[IpcProtocol.Links.Length..];
                // Ohne Text ist es nur die Frage, ob die App läuft.
                deliver = text.Length == 0 ? null : () => LinksReceived?.Invoke(text);
            }
            else return; // unbekannt: keine Bestätigung
            deliver?.Invoke();
            NativeMessage.Write(stream, Encoding.UTF8.GetBytes(IpcProtocol.Ok));
        }
        catch (Exception)
        {
            // eine kaputte Verbindung betrifft nur sich selbst
            client.Dispose();
        }
    }
}

/// <summary>Die Seite, die der laufenden App etwas sagt.</summary>
public static class IpcClient
{
    /// <summary>False, wenn keine App läuft oder sie nicht bestätigt.</summary>
    public static bool TryShow(RuntimePaths paths) => TrySend(paths, IpcProtocol.Show);

    /// <summary>Mit leerem Text nur die Frage, ob die App läuft.</summary>
    public static bool TrySendLinks(RuntimePaths paths, string text) => TrySend(paths, IpcProtocol.Links + text);

    private static bool TrySend(RuntimePaths paths, string message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        if (payload.Length > IpcProtocol.MaxMessage) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = socket.SendTimeout = (int)IpcProtocol.Timeout.TotalMilliseconds;
            socket.Connect(new UnixDomainSocketEndPoint(paths.Socket));
            using var stream = new NetworkStream(socket);
            NativeMessage.Write(stream, payload);
            return NativeMessage.Read(stream, 64) is { } reply && Encoding.UTF8.GetString(reply) == IpcProtocol.Ok;
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}
