using System.Threading.Channels;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
// Disambiguate: our own `Channel` namespace shadows System.Threading.Channels.
using SysChannel = System.Threading.Channels.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK PCAN-Basic adapter implementing <see cref="ICanChannel"/>. Wraps the
/// static <c>Peak.Can.Basic.BackwardCompatibility.PCANBasic</c> API for one
/// <see cref="ushort"/> channel handle.
/// <para>
/// Read path: a single background <see cref="Task"/> polls
/// <c>PCANBasic.Read</c> / <c>PCANBasic.ReadFD</c> until cancelled, raising
/// <see cref="FrameReceived"/> on the SDK thread. The internal
/// <see cref="Channel{T}"/> is reserved for follow-up work (e.g. back-pressure
/// or async streaming) and is currently written-to but not read-from.
/// </para>
/// <para>
/// Write path: <see cref="WriteAsync"/> formats the <see cref="CanFrame"/>
/// into a <c>TPCANMsg</c> / <c>TPCANMsgFD</c> and calls the synchronous
/// <c>PCANBasic.Write*</c>. The <c>TPCANMessageType</c> bit pattern selects
/// standard-vs-extended and FD-vs-classical; raw 11/29-bit IDs go into
/// <c>ID</c> without any IDE-bit flag.
/// </para>
/// <para>
/// Errors are translated via <see cref="PeakErrorMapper"/>; unexpected
/// exceptions are caught at the boundary and reported as
/// <see cref="ErrorCode.IoError"/> / <see cref="ErrorCode.HardwareNotAvailable"/>
/// rather than propagated, so the WPF ViewModel layer can render a message
/// without try/catch boilerplate.
/// </para>
/// </summary>
public sealed class PeakCanChannel : ICanChannel
{
    private readonly ushort _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<CanFrame> _internal = SysChannel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private Task? _readLoop;
    private readonly object _connectGate = new();

    public ChannelId Id { get; }
    public bool IsConnected { get; private set; }
    public event Action<CanFrame>? FrameReceived;

    public PeakCanChannel(ChannelId id)
    {
        Id = id;
        _handle = id.Handle;
    }

    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        // PCANBasic.Initialize is not thread-safe per handle — serialize.
        lock (_connectGate)
        {
            if (IsConnected)
            {
                return Result<Unit>.Fail(ErrorCode.InvalidState, $"Channel {Id} is already connected");
            }
            try
            {
                TPCANStatus status = fd
                    ? PCANBasic.InitializeFD(_handle, baud.Descriptor)
                    : ParseClassicBaudRate(baud, out var classicBaud)
                        ? PCANBasic.Initialize(_handle, classicBaud)
                        : throw new ArgumentException(
                            $"BaudRate '{baud.Name}' is not a valid classic CAN rate (use a string descriptor for FD-only rates).",
                            nameof(baud));
                if (PeakErrorMapper.IsOk((uint)status))
                {
                    IsConnected = true;
                }
                else
                {
                    return MakeError(status);
                }
            }
            catch (ArgumentException ex)
            {
                return Result<Unit>.Fail(ErrorCode.HardwareParameter, ex.Message);
            }
            catch (Exception ex)
            {
                return Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, ex.Message);
            }
        }
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), ct);
        return await Task.FromResult(Result<Unit>.Ok(default)).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return;
        _cts.Cancel();
        var loop = _readLoop;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on cancel */ }
        }
        try { PCANBasic.Uninitialize(_handle); }
        catch { /* swallow — the channel is going away regardless */ }
        IsConnected = false;
        _cts.Dispose();
    }

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
                    DATA = ToFixedBytes64(frame.Data),
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
                    DATA = ToFixedBytes8(frame.Data),
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

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // Drain both classic and FD queues. We loop on each separately so
        // a burst of one kind doesn't starve the other.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (PCANBasic.Read(_handle, out var msg, out var ts) == TPCANStatus.PCAN_ERROR_OK)
                {
                    EmitClassic(msg, ts);
                }
                while (PCANBasic.ReadFD(_handle, out var fdMsg, out var tsMicroseconds) == TPCANStatus.PCAN_ERROR_OK)
                {
                    EmitFd(fdMsg, tsMicroseconds);
                }
            }
            catch (Exception)
            {
                // SDK exceptions during read are surfaced as a channel event
                // failure at the WPF layer via a future `IFrameSink.OnError`
                // hook. For now, swallow and back off to avoid a hot loop.
            }
            try { await Task.Delay(1, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

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
        _internal.Writer.TryWrite(frame);
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
        var dlc = DlcToBytes(m.DLC);
        var bytes = new byte[dlc];
        Array.Copy(m.DATA, bytes, dlc);
        // TPCANTimestampFD in this SDK version is a plain UInt64 microsecond count.
        var frame = new CanFrame(canId, bytes, flags, Id, Timestamp.FromMicroseconds(tsMicroseconds));
        _internal.Writer.TryWrite(frame);
        FrameReceived?.Invoke(frame);
    }

    /// <summary>
    /// Map a <see cref="BaudRate"/> to a <see cref="TPCANBaudrate"/> enum value
    /// for the classic-CAN <c>Initialize</c> call. Returns false if the rate's
    /// descriptor doesn't match a known preset (caller should fall back to
    /// the FD path with the string descriptor).
    /// </summary>
    private static bool ParseClassicBaudRate(BaudRate rate, out TPCANBaudrate result)
    {
        if (rate.IsFd)
        {
            result = default;
            return false;
        }
        result = rate.Descriptor switch
        {
            "f_clock_mhz=20, nom_brp=8, nom_tseg1=8, nom_tseg2=3, nom_sjw=2" => TPCANBaudrate.PCAN_BAUD_125K,
            "f_clock_mhz=20, nom_brp=4, nom_tseg1=8, nom_tseg2=3, nom_sjw=2" => TPCANBaudrate.PCAN_BAUD_250K,
            "f_clock_mhz=20, nom_brp=2, nom_tseg1=8, nom_tseg2=3, nom_sjw=2" => TPCANBaudrate.PCAN_BAUD_500K,
            "f_clock_mhz=20, nom_brp=1, nom_tseg1=8, nom_tseg2=3, nom_sjw=2" => TPCANBaudrate.PCAN_BAUD_1M,
            _ => default,
        };
        return result != default;
    }

    private static byte DlcToBytes(byte dlc) => dlc switch
    {
        <= 8 => dlc,
        9 => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        _ => 64,
    };

    private static byte[] ToFixedBytes8(ReadOnlyMemory<byte> src)
    {
        var dst = new byte[8];
        src.Span.CopyTo(dst);
        return dst;
    }

    private static byte[] ToFixedBytes64(ReadOnlyMemory<byte> src)
    {
        var dst = new byte[64];
        src.Span.CopyTo(dst);
        return dst;
    }

    private static Result<Unit> MakeError(TPCANStatus s)
    {
        var (code, msg) = PeakErrorMapper.ToErrorCode((uint)s);
        return Result<Unit>.Fail(code, msg);
    }
}
