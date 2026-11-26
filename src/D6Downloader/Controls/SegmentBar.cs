using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using D6Downloader.Engine;

namespace D6Downloader.Controls;

/// <summary>
/// Dünner Fortschrittsbalken. Mit Segmenten zeigt er jedes Segment an seiner Stelle in der Datei, sonst den Anteil
/// <see cref="Fraction"/>. Gezeichnet in <see cref="Render"/>, ohne Vorlage und Animation.
/// </summary>
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<DownloadProgress?> ProgressProperty =
        AvaloniaProperty.Register<SegmentBar, DownloadProgress?>(nameof(Progress));
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(Fraction));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Track));

    static SegmentBar() => AffectsRender<SegmentBar>(ProgressProperty, FractionProperty, FillProperty, TrackProperty);

    public DownloadProgress? Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
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

        var p = Progress;
        if (p?.SegmentLengths is { Length: > 1 } lengths && p.TotalBytes > 0 && lengths.Length == p.SegmentBytes.Length)
        {
            using (context.PushClip(bounds))
            {
                double scale = w / p.TotalBytes;
                long start = 0;
                for (int i = 0; i < lengths.Length; i++)
                {
                    double width = Math.Min(p.SegmentBytes[i], lengths[i]) * scale;
                    if (width > 0) context.DrawRectangle(Fill, null, new Rect(start * scale, 0, width, h));
                    start += lengths[i];
                }
            }
            return;
        }

        double fill = w * Math.Clamp(Fraction, 0, 1);
        if (fill <= 0) return;
        using (context.PushClip(bounds))
            context.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, 0, Math.Max(fill, h), h), r));
    }
}
