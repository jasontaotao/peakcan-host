namespace PeakCan.Host.Core.HIL;

/// <summary>
/// HIL test execution mode — determines which path field of <see cref="HilRunRequest"/> is active.
/// </summary>
public enum HilMode
{
    /// <summary>Replay frames from a trace file (ASC/BLF).</summary>
    TraceReplay,

    /// <summary>Send/receive on real PCAN hardware.</summary>
    Hardware,

    /// <summary>Virtual ECU simulator driven by an ECU script JSON.</summary>
    VirtualEcu,

    /// <summary>Multi-ECU matrix driven by a matrix config JSON.</summary>
    Matrix,
}
