using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    // Flow B: NativeBindings (v1.0.0 + v3.16.9.4 PATCH + earlier).
    // 4 helpers that bridge PEAK SDK native types (TPCAN*) to peakcan-host
    // managed types (CanFrame, Result<Unit>, TPCANBaudrate). Sister of W14
    // CreateEngineFlow's V8 interop isolation pattern.
    //
    // Cross-flow callers (partial-class visible):
    //   - EmitClassic + EmitFd <- ReadLoopAsync (Flow A)
    //   - MakeError <- ConnectAsync (Flow main)
    //   - ResolveClassicCode <- ConnectAsync (Flow main)

    private void EmitClassic(TPCANMsg m, TPCANTimestamp ts)
    {
        var isExtended = (m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_EXTENDED) != 0;
        var canId = new CanId(m.ID, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var len = (byte)Math.Min((byte)m.LEN, (byte)8);
        var bytes = new byte[len];
        Array.Copy(m.DATA, bytes, len);
        var flags = FrameFlags.None;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_ERRFRAME) != 0) flags |= FrameFlags.ErrFrame;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_RTR) != 0) flags |= FrameFlags.Rtr;
        // TPCANTimestamp: millis (uint) + micros (ushort within ms).
        var frame = new CanFrame(canId, bytes, flags, Id, Timestamp.FromMillis(ts.millis, ts.micros));
        FrameReceived?.Invoke(frame);
    }

    private void EmitFd(TPCANMsgFD m, ulong tsMicroseconds)
    {
        var isExtended = (m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_EXTENDED) != 0;
        var canId = new CanId(m.ID, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = FrameFlags.Fd;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_BRS) != 0) flags |= FrameFlags.BitRateSwitch;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_ESI) != 0) flags |= FrameFlags.ErrorStateIndicator;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_ERRFRAME) != 0) flags |= FrameFlags.ErrFrame;
        var dlc = PeakCanFrameFormatter.DlcToBytes(m.DLC);
        var bytes = new byte[dlc];
        Array.Copy(m.DATA, bytes, dlc);
        // TPCANTimestampFD in this SDK version is a plain UInt64 microsecond count.
        var frame = new CanFrame(canId, bytes, flags, Id, Timestamp.FromMicroseconds(tsMicroseconds));
        FrameReceived?.Invoke(frame);
    }

    private static Result<Unit> MakeError(TPCANStatus s)
    {
        var (code, msg) = PeakErrorMapper.ToErrorCode((uint)s);
        return Result<Unit>.Fail(code, msg);
    }

    /// <summary>
    /// Map a Core <see cref="BaudRate"/> Name to the matching PEAK
    /// <c>TPCANBaudrate</c> enum. The classic <c>PCANBasic.Initialize</c>
    /// API does not accept the bitrate descriptor string; it only
    /// accepts the four <c>PCAN_BAUD_*</c> presets. Returns null for
    /// any name we don't recognize, which the caller maps to a
    /// <see cref="ErrorCode.HardwareParameter"/> failure.
    /// </summary>
    private static TPCANBaudrate? ResolveClassicCode(BaudRate baud) => baud.Name switch
    {
        "125 kbps" => TPCANBaudrate.PCAN_BAUD_125K,
        "250 kbps" => TPCANBaudrate.PCAN_BAUD_250K,
        "500 kbps" => TPCANBaudrate.PCAN_BAUD_500K,
        "1 Mbps" => TPCANBaudrate.PCAN_BAUD_1M,
        _ => null,
    };
}
