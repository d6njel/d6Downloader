using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using D6Downloader.Downloads;

namespace D6Downloader;

/// <summary>
/// Die Zeilen der Hauptliste: Downloads gefiltert nach der Seitenleiste, geordnet nach Statusgruppe (<see cref="StatusOrder{T}"/>),
/// mehrteilige Pakete unter einem Kopf (<see cref="PackageGrouping"/>). Umsortiert wird nur, wenn bei einem Download die Gruppe
/// wechselt, nie im 4-Hz-Takt, und nicht, solange die Oberfläche es sperrt (Menü offen, Maustaste gedrückt).
/// </summary>
public sealed class DownloadRows
{
    private readonly ItemCollection _items;
    private readonly Func<DownloadItem, bool> _filter;
    private readonly PackageGrouping _grouping;
    private readonly Func<bool> _blocked;
    private readonly Action<Action> _post;
    private readonly StatusOrder<DownloadItem> _order;
    private readonly HashSet<DownloadItem> _watched = new();
    private bool _queued;

    /// <param name="post">Reiht eine Aktion hinten in den UI-Thread ein (bündelt viele Änderungen zu einem Neuaufbau).</param>
    public DownloadRows(ItemCollection items, Func<DownloadItem, bool> filter, PackageGrouping grouping, Func<bool> blocked,
        Action<Action> post)
    {
        _items = items;
        _filter = filter;
        _grouping = grouping;
        _blocked = blocked;
        _post = post;
        _order = new StatusOrder<DownloadItem>(i => StatusGroups.Of(i.Status), i => _items.IndexOf(i));
        _items.CollectionChanged += OnItemsChanged;
        Watch();
        Refresh();
    }

    /// <summary>Downloads und Paketköpfe in Anzeigereihenfolge.</summary>
    public RangeCollection<object> Rows { get; } = new();

    /// <summary>Vor und nach jedem Abgleich: Die Liste merkt sich ihre Auswahl.</summary>
    public event Action? Rebuilding;

    public event Action? Rebuilt;

    public void Refresh()
    {
        // Gesperrt: Die Reihenfolge bleibt eingefroren, ein späterer Aufruf holt das Sortieren nach.
        if (_order.IsStale(_items) && !_blocked()) _order.Commit(_items);
        var ordered = _items.Where(_filter).ToList();
        ordered.Sort(_order.Compare);
        Rebuilding?.Invoke();
        PackageGrouping.Sync(Rows, _grouping.Build(ordered));
        Rebuilt?.Invoke();
    }

    /// <summary>Wie <see cref="Refresh"/>, aber gebündelt und etwas später.</summary>
    public void RequestRefresh()
    {
        if (_queued) return;
        _queued = true;
        _post(() =>
        {
            _queued = false;
            Refresh();
        });
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Watch();
        RequestRefresh();
    }

    private void Watch()
    {
        var now = new HashSet<DownloadItem>(_items);
        foreach (var gone in _watched.Where(i => !now.Contains(i)).ToList())
        {
            gone.PropertyChanged -= OnItemChanged;
            _watched.Remove(gone);
        }
        foreach (var item in now)
            if (_watched.Add(item)) item.PropertyChanged += OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.Status) or nameof(DownloadItem.Category) or nameof(DownloadItem.QueueId))
            RequestRefresh();
    }
}
