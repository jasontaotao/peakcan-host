namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Direction of fault injection: send path, receive path, or both.
/// </summary>
public enum FaultDirection
{
    /// <summary>Inject faults into the send path (WriteAsync).</summary>
    Send,

    /// <summary>Inject faults into the receive path (FrameReceived).</summary>
    Receive,

    /// <summary>Inject faults into both send and receive paths.</summary>
    Both,
}
