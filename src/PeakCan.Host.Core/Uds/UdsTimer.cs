namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS timeout management. Configurable P2/P2*/S3/P3* timers.
/// </summary>
public sealed class UdsTimer
{
    /// <summary>P2 timeout (ms) — time between request and response.</summary>
    public TimeSpan P2Timeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>P2* timeout (ms) — time after NRC 0x78.</summary>
    public TimeSpan P2StarTimeout { get; set; } = TimeSpan.FromMilliseconds(5000);

    /// <summary>S3 timeout (ms) — session timeout.</summary>
    public TimeSpan S3Timeout { get; set; } = TimeSpan.FromMilliseconds(5000);

    /// <summary>P3* timeout (ms) — response pending timeout.</summary>
    public TimeSpan P3StarTimeout { get; set; } = TimeSpan.FromMilliseconds(5000);
}
