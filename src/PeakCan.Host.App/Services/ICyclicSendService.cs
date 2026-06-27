using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.2.11 PATCH: abstraction over <see cref="CyclicSendService"/> so the
/// SendViewModel can be unit-tested with a fake without driving real timers
/// or the PEAK SDK. Mirrors the public surface area the VM needs.
/// </summary>
public interface ICyclicSendService
{
    /// <summary>True when the cyclic send timer is active.</summary>
    bool IsRunning { get; }

    /// <summary>Number of frames sent since the last <see cref="Start"/>.</summary>
    long SendCount { get; }

    /// <summary>
    /// Start cyclic transmission of <paramref name="frame"/> at
    /// <paramref name="interval"/>. If already running, stops first.
    /// </summary>
    void Start(CanFrame frame, TimeSpan interval);

    /// <summary>Stop cyclic transmission. Idempotent.</summary>
    void Stop();
}