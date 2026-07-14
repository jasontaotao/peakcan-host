// PeakCanChannel/WriteFlow.partial.cs — W35 T2 (Flow B, 47 LoC)
// Public async method: WriteAsync dispatches the CanFrame write to
// PCAN-Basic via classic (TPCANMsg + Write) or FD (TPCANMsgFD + WriteFD)
// based on frame.IsFd. Returns Result<Unit> from the W18 NativeBindings
// partial's MakeError helper.
//
// W18 PeakCanChannel NativeBindings.cs has MakeError (also extracted in W18).
// W35 cross-partial caller: WriteAsync calls MakeError from NativeBindings.cs
// partial (W18 sister).
//
// Cross-partial visibility:
//   - WriteAsync (this partial) reads _handle + IsConnected (in main partial);
//     uses PeakCanFrameFormatter.ToFixedBytes64 + ToFixedBytes8 (static methods
//     in PeakCan.Host.Infrastructure.Channel namespace, NOT partial); calls MakeError (NativeBindings
//     partial).
//
// W23 STRUCT-FABRACTION LESSON 18th observation — signatures verified:
//   - TPCANStatus enum (returned by PCANBasic.Write / WriteFD, mapped via MakeError)
//   - TPCANMessageType flag-bit pattern: PCAN_MESSAGE_FD | PCAN_MESSAGE_EXTENDED |
//     PCAN_MESSAGE_STANDARD | PCAN_MESSAGE_BRS | PCAN_MESSAGE_ESI
//   - TPCANMsgFD struct (ID:uint + MSGTYPE:TPCANMessageType + DLC:byte + DATA:byte[])
//     used in the FD branch — fields set via object initializer
//   - TPCANMsg struct (ID:uint + MSGTYPE:TPCANMessageType + LEN:byte + DATA:byte[])
//     used in the classic branch — fields set via object initializer
//   - PeakCanFrameFormatter.ToFixedBytes8(CanFrame.Data) returns byte[8]
//   - PeakCanFrameFormatter.ToFixedBytes64(CanFrame.Data) returns byte[64]
//   - FrameFlags.BitRateSwitch + FrameFlags.ErrorStateIndicator bitflags
//   - CanFrame.IsFd property (boolean dispatch discriminator)
//   - CanFrame.Id.IsExtended property (boolean — picks EXTENDED vs STANDARD msgType)
//   - CanFrame.Id.Raw property (uint — passed to ID field)
//   - CanFrame.Dlc property (byte — clamped via Math.Min)
//   - MakeError(TPCANStatus) static method in W18 NativeBindings.cs partial —
//     cross-partial reference (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30
//     +W31+W32+W33+W34 cross-partial helper pattern)
//
// 4 [LoggerMessage] declarations stay on main partial per
// W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 sister precedent
// (CS8795 mitigation). WriteFlow does NOT call any logger partial directly
// (no logger calls in WriteAsync per main HEAD L174-L220 verification).
//
// W19 R1 LESSON ENHANCED recovery procedure (pre-flight prevention + post-failure
// recovery):
//   - Pre-flight: re-grep boundaries BEFORE running the deletion script via
//     `git show HEAD:src/.../PeakCanChannel.cs | grep -n "WriteAsync\|^}"` to
//     confirm post-T1 line numbers (HEAD == T1 commit == post-T1 state).
//   - Post-failure: if LoC delta after deletion is OUTSIDE ±2 tolerance:
//     1. `git checkout HEAD -- src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs`
//        (restore from HEAD since HEAD = T1 commit = post-T1 state)
//     2. Re-grep post-T1 boundaries
//     3. Correct the offsets in the script
//     4. Re-run the script (NEVER do this in a reviewer's read-only pass — only
//        from implementer context)
//     5. Verify delta = expected (47 ±2)
//
// W20 LESSON 47th application (verbatim re-extraction):
//   Run `git show main:src/.../PeakCanChannel.cs | sed -n '174,220p'` for the
//   verbatim source of `WriteAsync` body. **Use `main` not `HEAD`** because
//   `main` has the original L174-L220 boundaries. The CURRENT file (post-T1)
//   has the same body but at shifted lines (post-T1 L111-L157).
//   Do NOT re-type or "improve" the code. Verbatim copy.
//
// W35 T2 verbatim re-extracted via
//   `git show main:src/.../PeakCanChannel.cs | sed -n '174,220p'`
// per W20 T2 R1 fabrication LESSON (47th application).

using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    /// <summary>
    /// Write <paramref name="frame"/> to the underlying PCAN-Basic handle.
    /// Calls <c>PCANBasic.Write</c> for classic frames or <c>PCANBasic.WriteFD</c>
    /// for FD frames, translating <paramref name="frame"/>'s
    /// <see cref="CanFrame.Id"/> + <see cref="CanFrame.Flags"/> + <see cref="CanFrame.Data"/>
    /// into the SDK's <c>TPCANMsg</c> / <c>TPCANMsgFD</c> representation.
    /// </summary>
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Not connected"));
        }
        try
        {
            TPCANStatus status;
            if (frame.IsFd)
            {
                var msgType = TPCANMessageType.PCAN_MESSAGE_FD
                              | (frame.Id.IsExtended ? TPCANMessageType.PCAN_MESSAGE_EXTENDED : TPCANMessageType.PCAN_MESSAGE_STANDARD);
                if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) msgType |= TPCANMessageType.PCAN_MESSAGE_BRS;
                if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) msgType |= TPCANMessageType.PCAN_MESSAGE_ESI;
                var m = new TPCANMsgFD
                {
                    ID = frame.Id.Raw,
                    MSGTYPE = msgType,
                    DLC = (byte)Math.Min(frame.Dlc, (byte)15),
                    DATA = PeakCanFrameFormatter.ToFixedBytes64(frame.Data),
                };
                status = PCANBasic.WriteFD(_handle, ref m);
            }
            else
            {
                var msgType = frame.Id.IsExtended
                    ? TPCANMessageType.PCAN_MESSAGE_EXTENDED
                    : TPCANMessageType.PCAN_MESSAGE_STANDARD;
                var m = new TPCANMsg
                {
                    ID = frame.Id.Raw,
                    MSGTYPE = msgType,
                    LEN = (byte)Math.Min(frame.Dlc, (byte)8),
                    DATA = PeakCanFrameFormatter.ToFixedBytes8(frame.Data),
                };
                status = PCANBasic.Write(_handle, ref m);
            }
            return PeakErrorMapper.IsOk((uint)status)
                ? ValueTask.FromResult(Result<Unit>.Ok(default))
                : ValueTask.FromResult(MakeError(status));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, ex.Message));
        }
    }
}