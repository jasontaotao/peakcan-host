using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class PlatformUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);

    public IDisposable StartTimer(TimeSpan period, Action tick)
    {
        var timer = Application.Current!.Dispatcher.CreateTimer();
        timer.Interval = period;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new TimerStopper(timer);
    }

    private sealed class TimerStopper(IDispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
