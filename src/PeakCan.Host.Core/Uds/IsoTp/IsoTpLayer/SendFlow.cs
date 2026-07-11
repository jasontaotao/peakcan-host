using System.Threading;
using System.Threading.Tasks;

namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow B: Send (v1.2.12 PATCH Item 2 + v1.2.13 PATCH Item 5).
    // 4 methods moved verbatim from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - SendMessageAsync -> SendSingleFrameAsync (intra-flow, SF path)
    //                      -> SendMultiFrameAsync (Flow C, MF path)
    //   - SendSingleFrameAsync -> SendCanFrameAsync (intra-flow)
    //   - SendCanFrameAsync -> LogIsoTpSendFailed (Flow G) + SendFailureCount (test counter, main)
    //   - SendCanFrame <- SendFlowControl (Flow F, RX-side FC)

    /// <summary>
    /// Send a message, segmenting it into multiple CAN frames if needed.
    /// </summary>
    /// <param name="data">Message payload (up to 4095 bytes).</param>
    /// <exception cref="ArgumentException">Payload too long.</exception>
    public async Task SendMessageAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length > MaxMessageLength)
            throw new ArgumentException($"Payload too long: {data.Length} > {MaxMessageLength}");

        if (data.Length <= MaxSingleFramePayload)
        {
            // Single Frame — route through the async send helper so the
            // async-ctor path actually delivers frames. The previous sync
            // SendCanFrame path silently dropped SF frames when only the
            // async callback was wired (v1.2.12 latent production bug M-6).
            await SendSingleFrameAsync(data).ConfigureAwait(false);
        }
        else
        {
            // Multi-frame: FF + CFs
            await SendMultiFrameAsync(data, ct).ConfigureAwait(false);
        }
    }

    private Task SendSingleFrameAsync(byte[] data)
    {
        var frame = new IsoTpFrame(IsoTpFrameType.Single, data: data);
        var canData = frame.Encode();
        return SendCanFrameAsync(canData, frameIndex: 0);
    }

    /// <summary>
    /// v1.2.13 PATCH Item 5: dispatch the encoded CAN frame through whichever
    /// callback the caller wired up. The async callback is awaited so an
    /// SDK hang is bounded by the layer's own timeouts (FC timeout, BS gate)
    /// instead of by <c>.AsTask().Wait()</c> on the SDK read thread.
    /// <para>
    /// On send-callback failure: log at Error (preserves the v1.2.12
    /// behaviour), increment <see cref="SendFailureCount"/>, and throw
    /// <see cref="IsoTpSendFailedException"/>. The throw propagates up
    /// through <see cref="SendMultiFrameAsync"/>, aborting the CF burst on
    /// the first failure (so a bus-off mid-FF no longer silently drops
    /// all subsequent CFs). The single-frame (TesterPresent) path passes
    /// <c>frameIndex: 0</c> and is allowed to throw; UdsClient / App
    /// factory catch sites handle it.
    /// </para>
    /// </summary>
    /// <param name="data">Encoded ISO-TP frame payload.</param>
    /// <param name="frameIndex">Position in the multi-frame burst (0 for FF/SF, 1..N for CF).</param>
    private async Task SendCanFrameAsync(byte[] data, int frameIndex)
    {
        var frame = new CanFrame(
            new CanId(_config.RequestId, FrameFormat.Standard),
            data,
            FrameFlags.None,
            default,
            default);

        if (_sendFrameAsync is not null)
        {
            try
            {
                await _sendFrameAsync(frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // v1.2.13 PATCH Item 5: propagate as IsoTpSendFailedException
                // so the caller (multi-frame transport, UDS layer, App
                // factory) can abort on the first failure instead of
                // silently dropping all subsequent CFs.
                if (_logger is not null)
                    LogIsoTpSendFailed(_logger, ex, frame.Id.Raw);
                Interlocked.Increment(ref SendFailureCount);
                throw new IsoTpSendFailedException(frame.Id.Raw, frameIndex, ex);
            }
            return;
        }

        // Legacy sync path: callers using the Action<CanFrame> ctor keep
        // their existing fire-and-forget semantics.
        _sendFrame?.Invoke(frame);
    }

    private void SendCanFrame(byte[] data)
    {
        var frame = new CanFrame(
            new CanId(_config.RequestId, FrameFormat.Standard),
            data,
            FrameFlags.None,
            default,
            default);

        // Legacy sync path. In practice the async ctor is the new default,
        // so the old Action<CanFrame> callback is set when this method runs.
        _sendFrame?.Invoke(frame);
    }
}