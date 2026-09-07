namespace PeakCan.Host.Mobile.Core.Platform;

/// <summary>UI-thread marshalling + recurring UI-thread timer abstraction.
/// Implemented per-platform (MAUI on Android). VM depends on this interface,
/// not MAUI Essentials, so it is unit-testable under plain net10.0.</summary>
public interface IUiDispatcher
{
    /// <summary>Run <paramref name="action"/> on the UI thread (inline if already on it).</summary>
    void Post(Action action);

    /// <summary>Start a recurring UI-thread timer firing <paramref name="tick"/> every <paramref name="period"/>. Dispose to stop.</summary>
    IDisposable StartTimer(TimeSpan period, Action tick);
}
