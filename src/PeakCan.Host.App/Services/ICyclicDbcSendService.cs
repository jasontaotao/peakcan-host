using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.5.1 PATCH Item 2 (Periodic DBC send): abstraction over
/// <see cref="CyclicDbcSendService"/> so the <c>DbcSendViewModel</c> can
/// be unit-tested with a NSubstitute mock without driving real timers or
/// the PEAK SDK. Mirrors <see cref="ICyclicSendService"/>'s public surface.
/// </summary>
public interface ICyclicDbcSendService
{
    /// <summary>True when the cyclic DBC send timer is active.</summary>
    bool IsRunning { get; }

    /// <summary>Frames the channel reported as successfully transmitted since the last <see cref="Start"/>.</summary>
    long SuccessCount { get; }

    /// <summary>Frames the encoder failed to encode + frames the channel reported as failed since the last <see cref="Start"/>.</summary>
    long FailureCount { get; }

    /// <summary>
    /// Start periodic transmission of a DBC-encoded frame. The
    /// <paramref name="frameProvider"/> is invoked on each tick: returns
    /// the (Message, signalValues) pair from the current UI selection so
    /// that user edits to the signal DataGrid flow into the periodic
    /// encode path naturally. If the provider's
    /// <see cref="Message.Id"/> differs from the captured Message.Id, the
    /// service stops itself (one-time leak surfaced via
    /// <see cref="FailureCount"/>++) — see <c>CyclicDbcSendService</c>
    /// Decision 9.
    /// </summary>
    void Start(
        Func<(Message message, IReadOnlyDictionary<string, double> values)> frameProvider,
        TimeSpan interval);

    /// <summary>Stop periodic transmission. Idempotent.</summary>
    void Stop();
}
