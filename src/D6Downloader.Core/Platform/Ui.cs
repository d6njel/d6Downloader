using System;

namespace D6Downloader.Platform;

/// <summary>
/// Wiederkehrender Timer, dessen <see cref="Tick"/> auf dem UI-Thread kommt. <see cref="Start"/> nach <see cref="Stop"/>
/// beginnt das Intervall von vorn.
/// </summary>
public interface IUiTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}

public interface ITimerFactory
{
    /// <param name="urgent">Nicht hinter anderer Arbeit der Oberfläche anstellen (Countdown vor dem Ausschalten).</param>
    IUiTimer Create(TimeSpan interval, bool urgent = false);
}

/// <summary>Reiht Arbeit in den UI-Thread ein, ohne zu warten. Von jedem Thread aus aufrufbar.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
