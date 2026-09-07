using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Core.Tests.Fakes;

/// <summary>Inline dispatcher + controllable timer. Post runs immediately; StartTimer fires tick only on Tick().</summary>
public sealed class FakeUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
    public IDisposable StartTimer(TimeSpan period, Action tick) => new FakeTimer(period, tick);

    public sealed class FakeTimer(TimeSpan period, Action tick) : IDisposable
    {
        public TimeSpan Period { get; } = period;
        private bool _disposed;
        public void Tick()
        {
            if (!_disposed) tick();
        }

        public void Dispose() => _disposed = true;
    }
}
