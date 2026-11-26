using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using D6Downloader.Downloads;

namespace D6Downloader.Controls;

/// <summary>Verlauf der Geschwindigkeit: Fläche mit Linie, neuester Wert rechts. Zeichnet nur, wenn ein Wert dazukommt.</summary>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<SpeedHistory?> HistoryProperty =
        AvaloniaProperty.Register<SpeedGraph, SpeedHistory?>(nameof(History));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> BaselineProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Baseline));

    /// <summary>
    /// Breite je Wert in Pixeln, sobald der Verlauf die Breite füllt; 0 = der ganze Verlauf
    /// (<see cref="SpeedHistory.Capacity"/> Werte) füllt die Breite, neue Werte schieben von rechts hinein.
    /// </summary>
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<SpeedGraph, double>(nameof(Step));

    /// <summary>Faktor auf den höchsten Wert, damit der Verlauf nicht oben anstößt.</summary>
    public static readonly StyledProperty<double> HeadroomProperty =
        AvaloniaProperty.Register<SpeedGraph, double>(nameof(Headroom), 1.2);

    static SpeedGraph() =>
        AffectsRender<SpeedGraph>(HistoryProperty, StrokeProperty, FillProperty, BaselineProperty, StepProperty, HeadroomProperty);

    private SpeedHistory? _subscribed;
    private bool _attached;

    public SpeedHistory? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Baseline { get => GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double Headroom { get => GetValue(HeadroomProperty); set => SetValue(HeadroomProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HistoryProperty) Subscribe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Subscribe(); // der Verlauf lebt länger als die Ansicht
    }

    private void Subscribe()
    {
        var wanted = _attached ? History : null;
        if (ReferenceEquals(wanted, _subscribed)) return;
        if (_subscribed is not null) _subscribed.Changed -= OnSample;
        _subscribed = wanted;
        if (wanted is not null) wanted.Changed += OnSample;
        InvalidateVisual();
    }

    private void OnSample(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var history = History;
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // Fläche für Tooltips
        if (Baseline is not null) context.DrawRectangle(Baseline, null, new Rect(0, h - 1, w, 1));
        if (history is null || history.Count < 2) return;

        int count = history.Count;
        double step = Step;
        double dx = w / (SpeedHistory.Capacity - 1);
        int first = 0;
        double max = Math.Max(history.Max, 1);
        if (step > 0)
        {
            // Mit fester Schrittweite passen nur die neuesten Werte hinein: Der Maßstab richtet sich nach ihnen.
            int fit = (int)(w / step);
            dx = count - 1 > fit ? step : w / (count - 1);
            first = Math.Max(0, count - 1 - fit);
            max = 1;
            for (int i = first; i < count; i++) max = Math.Max(max, history[i]);
        }
        max *= Math.Max(1, Headroom);
        double X(int i) => w - (count - 1 - i) * dx;
        double Y(int i) => h - 1 - history[i] / max * (h - 2);

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(X(first), h), true);
            for (int i = first; i < count; i++) g.LineTo(new Point(X(i), Y(i)));
            g.LineTo(new Point(w, h));
            g.EndFigure(true);
        }
        context.DrawGeometry(Fill, null, area);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(X(first), Y(first)), false);
            for (int i = first + 1; i < count; i++) g.LineTo(new Point(X(i), Y(i)));
            g.EndFigure(false);
        }
        if (Stroke is not null) context.DrawGeometry(null, new Pen(Stroke, 1.25, lineJoin: PenLineJoin.Round), line);
    }
}
