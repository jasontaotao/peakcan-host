using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.2.11 PATCH: abstraction over <see cref="CyclicSendService"/> so the
/// SendViewModel can be unit-tested with a fake without driving real timers
/// or the PEAK SDK. Mirrors the public surface area the VM needs.
/// <para>
/// v1.2.12 PATCH Item 10: counters split into <see cref="SuccessCount"/> +
/// <see cref="FailureCount"/> so the UI can distinguish the two outcomes
/// (v1.5.1 PATCH Item 3: removed the obsolete mixed <c>SendCount</c>).
/// </para>
/// </summary>
public interface ICyclicSendService
{
    /// <summary>True when the cyclic send timer is active.</summary>
    bool IsRunning { get; }

    /// <summary>Frames the channel reported as successfully transmitted since the last <see cref="Start"/>.</summary>
    long SuccessCount { get; }

    /// <summary>Frames the channel reported as failed since the last <see cref="Start"/>.</summary>
    long FailureCount { get; }

    /// <summary>
    /// Start cyclic transmission of <paramref name="frame"/> at
    /// <paramref name="interval"/>. If already running, stops first.
    /// </summary>
    void Start(CanFrame frame, TimeSpan interval);

    /// <summary>Stop cyclic transmission. Idempotent.</summary>
    void Stop();
}
