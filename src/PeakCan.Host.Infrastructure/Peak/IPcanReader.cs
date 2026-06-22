using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// Abstraction over the PEAK SDK read calls so
/// <see cref="PeakCanChannel.ReadLoopAsync"/> can be unit-tested without
/// real hardware. Production injects <see cref="PcanReader"/>; tests
/// inject a fake that yields canned frames.
/// <para>
/// <b>Why not return <see cref="CanFrame"/> directly?</b> the SDK
/// returns <c>TPCANMsg</c> / <c>TPCANMsgFD</c> structs with metadata
/// (MSGTYPE flags, DLC codes) that the channel must translate. Keeping
/// the raw types in the interface lets us test the translation logic
/// itself (EmitClassic / EmitFd) without conflating it with the read
/// loop's retry / backoff behaviour.
/// </para>
/// </summary>
public interface IPcanReader
{
    /// <summary>
    /// Read one classic CAN frame from <paramref name="handle"/>.
    /// Returns <c>PCAN_ERROR_OK</c> if a frame was read;
    /// <c>PCAN_ERROR_QRCVEMPTY</c> if the queue is empty (caller
    /// should sleep and retry). Any other status is a hardware error.
    /// </summary>
    TPCANStatus ReadClassic(ushort handle, out TPCANMsg msg, out TPCANTimestamp ts);

    /// <summary>
    /// Read one CAN FD frame from <paramref name="handle"/>.
    /// Same contract as <see cref="ReadClassic"/>.
    /// </summary>
    TPCANStatus ReadFd(ushort handle, out TPCANMsgFD msg, out ulong tsMicroseconds);
}

/// <summary>
/// Production <see cref="IPcanReader"/> that delegates to the static
/// <c>PCANBasic.Read</c> / <c>PCANBasic.ReadFD</c> API.
/// </summary>
public sealed class PcanReader : IPcanReader
{
    public TPCANStatus ReadClassic(ushort handle, out TPCANMsg msg, out TPCANTimestamp ts)
        => PCANBasic.Read(handle, out msg, out ts);

    public TPCANStatus ReadFd(ushort handle, out TPCANMsgFD msg, out ulong tsMicroseconds)
        => PCANBasic.ReadFD(handle, out msg, out tsMicroseconds);
}
