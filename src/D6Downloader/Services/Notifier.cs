using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace D6Downloader.Services;

/// <summary>Meldungen der Arbeitsumgebung und die Frage, ob es einen Tray gibt.</summary>
public interface INotifier : IDisposable
{
    /// <param name="onClick">Läuft, wenn der Benutzer die Meldung anklickt, auf einem beliebigen Thread. Null = keine Aktion.</param>
    void Notify(string title, string body, bool error = false, Action? onClick = null);

    /// <summary>Nimmt jemand Tray-Symbole an (StatusNotifierWatcher)? Nur dann darf das Fenster ganz verschwinden.</summary>
    Task<bool> HasTrayAsync();
}

/// <summary>
/// <c>org.freedesktop.Notifications</c> über den Sitzungsbus. Alles ist freiwillig: Ohne Bus oder Benachrichtigungsdienst geschieht
/// einfach nichts.
/// </summary>
public sealed class DbusNotifier : INotifier
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private readonly object _gate = new();
    private readonly Dictionary<uint, Action> _clicks = new();
    private Task<DBusConnection?>? _connection;
    private IDisposable? _signal;
    private bool _disposed;

    public async void Notify(string title, string body, bool error = false, Action? onClick = null)
    {
        try
        {
            if (await Connect().ConfigureAwait(false) is not { } connection) return;
            uint id = await connection.CallMethodAsync(NotifyMessage(connection, title, body, error, onClick is not null),
                static (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null).ConfigureAwait(false);
            if (onClick is null) return;
            lock (_gate)
            {
                if (_clicks.Count > 32) _clicks.Clear();
                _clicks[id] = onClick;
            }
        }
        catch (Exception)
        {
            // keine Meldung ist kein Fehler des Programms
        }
    }

    private static MessageBuffer NotifyMessage(DBusConnection connection, string title, string body, bool error, bool clickable)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, member: "Notify",
                signature: "susssasa{sv}i");
            writer.WriteString("d6Downloader");
            writer.WriteUInt32(0);
            writer.WriteString("d6downloader");
            writer.WriteString(title);
            writer.WriteString(Escape(body)); // der Text kann Markup enthalten (Dateinamen vom Server)
            writer.WriteArray(clickable ? new[] { "default", "OK" } : Array.Empty<string>());
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["urgency"] = VariantValue.Byte(error ? (byte)2 : (byte)1),
                ["desktop-entry"] = VariantValue.String("d6downloader"),
            });
            writer.WriteInt32(-1);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public async Task<bool> HasTrayAsync()
    {
        try
        {
            if (await Connect().ConfigureAwait(false) is not { } connection) return false;
            var names = await connection.ListServicesAsync().ConfigureAwait(false);
            return Array.IndexOf(names, "org.kde.StatusNotifierWatcher") >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Task<DBusConnection?> Connect()
    {
        lock (_gate) return _connection ??= Task.Run(ConnectAsync);
    }

    private async Task<DBusConnection?> ConnectAsync()
    {
        try
        {
            if (DBusAddress.Session is not { } address) return null;
            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            try
            {
                _signal = await connection.WatchSignalAsync(Service, ObjectPath, Service, "ActionInvoked",
                    static (Message m, object? _) => m.GetBodyReader().ReadUInt32(),
                    (Notification<uint> signal) => { if (signal.HasValue) Clicked(signal.Value); },
                    emitOnCapturedContext: false, flags: ObserverFlags.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ohne Signal gibt es Meldungen ohne Klick
            }
            if (_disposed) connection.Dispose();
            return connection;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Clicked(uint id)
    {
        Action? action;
        lock (_gate)
        {
            if (!_clicks.Remove(id, out action)) return;
        }
        action();
    }

    public void Dispose()
    {
        _disposed = true;
        Task<DBusConnection?>? connection;
        lock (_gate) connection = _connection;
        _signal?.Dispose();
        if (connection is { IsCompletedSuccessfully: true }) connection.Result?.Dispose();
    }
}
