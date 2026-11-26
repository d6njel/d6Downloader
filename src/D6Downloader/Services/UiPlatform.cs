using System;
using Avalonia.Threading;
using D6Downloader.Platform;

namespace D6Downloader.Services;

/// <summary>Timer und UI-Thread von Avalonia für den <c>DownloadManager</c>.</summary>
public sealed class AvaloniaTimerFactory : ITimerFactory
{
    public IUiTimer Create(TimeSpan interval, bool urgent = false) =>
        new Timer(new DispatcherTimer(urgent ? DispatcherPriority.Normal : DispatcherPriority.Background) { Interval = interval });

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(DispatcherTimer timer)
        {
            _timer = timer;
            _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        }

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public bool IsEnabled => _timer.IsEnabled;
        public event EventHandler? Tick;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
