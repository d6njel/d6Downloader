using System;
using System.Collections.Generic;

namespace D6Downloader.Services;

/// <summary>
/// Nimmt Nachrichten vom Socket an (Thread-Pool) und hält sie, bis die Oberfläche sie abholt. Der Empfänger muss selbst auf den
/// UI-Thread wechseln.
/// </summary>
public sealed class IpcInbox
{
    private readonly object _gate = new();
    private readonly List<string?> _waiting = new(); // null = nur Fenster zeigen
    private Action<string?>? _receiver;

    public void Show() => Deliver(null);

    public void Links(string text) => Deliver(text);

    /// <summary>Ab jetzt gehen Nachrichten an <paramref name="receiver"/>, zuerst die wartenden.</summary>
    public void Attach(Action<string?> receiver)
    {
        lock (_gate)
        {
            _receiver = receiver;
            foreach (string? message in _waiting) receiver(message);
            _waiting.Clear();
        }
    }

    private void Deliver(string? message)
    {
        lock (_gate)
        {
            if (_receiver is null)
            {
                if (_waiting.Count < 64) _waiting.Add(message);
            }
            else _receiver(message);
        }
    }
}
