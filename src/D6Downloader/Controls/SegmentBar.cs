using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace D6Downloader.Controls;

/// <summary>
/// Dünner Fortschrittsbalken. Füllt sich lückenlos von links nach dem Anteil <see cref="Fraction"/>. Gezeichnet in
/// <see cref="Render"/>, ohne Vorlage und Animation.
/// </summary>
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(Fraction));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Track));

    static SegmentBar() => AffectsRender<SegmentBar>(FractionProperty, FillProperty, TrackProperty);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        float r = (float)(h / 2);
        var bounds = new RoundedRect(new Rect(0, 0, w, h), r);
        context.DrawRectangle(Track, null, bounds);

        double fill = w * Math.Clamp(Fraction, 0, 1);
        if (fill <= 0) return;
        using (context.PushClip(bounds))
            context.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, 0, Math.Max(fill, h), h), r));
    }
}
