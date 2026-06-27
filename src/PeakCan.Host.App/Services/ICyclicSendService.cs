using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.2.11 PATCH: abstraction over <see cref="CyclicSendService"/> so the
/// SendViewModel can be unit-tested with a fake without driving real timers
/// or the PEAK SDK. Mirrors the public surface area the VM needs.
/// <para>
/// v1.2.12 PATCH Item 10: <see cref="SendCount"/> is now <c>[Obsolete]</c>
/// — consumers should bind <see cref="SuccessCount"/> + <see cref="FailureCount"/>
/// separately so the UI can distinguish the two outcomes.
/// </para>
/// </summary>
public interface ICyclicSendService
{
    /// <summary>True when the cyclic send timer is active.</summary>
    bool IsRunning { get; }

    /// <summary>Total frames sent since the last <see cref="Start"/>.</summary>
    [Obsolete("Use SuccessCount + FailureCount. v1.2.12 split the mixed counter; remove in v1.2.13.")]
    long SendCount { get; }

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