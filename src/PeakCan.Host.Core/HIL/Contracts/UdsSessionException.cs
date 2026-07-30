namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Base exception for all IUdsSession failures. Defined in Core/HIL/Contracts
/// so executors can catch without referencing Core.Uds.
/// </summary>
public abstract class UdsSessionException : Exception
{
    protected UdsSessionException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Thrown by IUdsSession.SendRequestAsync when ECU returns a Negative Response.
/// </summary>
public sealed class UdsNrcException : UdsSessionException
{
    public byte ServiceId { get; }
    public byte Nrc { get; }
    public UdsNrcException(byte serviceId, byte nrc)
        : base($"NRC 0x{nrc:X2} from service 0x{serviceId:X2}")
    {
        ServiceId = serviceId;
        Nrc = nrc;
    }
}

/// <summary>
/// Thrown when UDS request times out or transport fails (not an NRC).
/// </summary>
public sealed class UdsSessionTransportException : UdsSessionException
{
    public UdsSessionTransportException(string message, Exception? inner = null)
        : base(message, inner) { }
}
