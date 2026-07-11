using System.Threading;
using System.Threading.Tasks;

namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow C: MultiFrameTransport (v1.2.12 PATCH Item 2 + v1.2.13 PATCH Item 5 + v1.2.14 PATCH Task 1).
    // 3 methods moved verbatim from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - SendMultiFrameAsync -> SendCanFrameAsync (Flow B, cross-partial) + WaitForFlowControlAsync (intra-flow) + StMinToTimeSpan (intra-flow)
    //   - WaitForFlowControlAsync -> _flowControlTimeout (state, main) + _txLock + _txWaitingForFc (state, main)
    //   - StMinToTimeSpan -> _txStMin (state, main)
    //
    // LARGEST flow in IsoTpLayer (~146 LoC, ~19% of original 806 LoC).

    private async Task SendMultiFrameAsync(byte[] data, CancellationToken ct)
    {
        // v1.2.12 PATCH Item 2: serialize concurrent multi-frame sends so the
        // FF/CF sequence of one transport cannot interleave with another's.
        // We hold _sendGate for the whole multi-frame transport (FF → FC
        // wait → CFs → BS gate → ... → done). Single-frame sends use the
        // SendCanFrame async path but are not gated here (they are rare in
        // practice and ISO-TP §6.4 already covers arbitration semantics).
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_txLock)
            {
                _txWaitingForFc = true;
            }
            // v1.2.13 PATCH Item 5: reset the per-transport CF counter so
            // frameIndex starts at 1 for the first CF in this transport.
            // _sendGate serializes transports so this is safe (no other
            // transport can be in flight).
            _cfCounter = 0;

            // Send First Frame (frameIndex=0 by convention for FF).
            var ffData = data.AsMemory(0, Math.Min(6, data.Length));
            var ff = new IsoTpFrame(IsoTpFrameType.First, length: data.Length, data: ffData);
            await SendCanFrameAsync(ff.Encode(), frameIndex: 0).ConfigureAwait(false);

            // Wait for Flow Control
            if (!await WaitForFlowControlAsync(ct).ConfigureAwait(false))
                throw new TimeoutException("No Flow Control received");

            // Send Consecutive Frames. Honour the negotiated Block Size (BS):
            // when BS>0, pause after every BS-th CF and wait for the next FC.
            // BS=0 means "send all remaining CFs without further FC".
            int offset = 6;
            int sequence = 1;
            int cfInBlock = 0;
            int bs;
            lock (_txLock) { bs = _txBlockSize; }

            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                int chunkSize = Math.Min(7, data.Length - offset);
                var cfData = data.AsMemory(offset, chunkSize);
                var cf = new IsoTpFrame(IsoTpFrameType.Consecutive, sequenceOrStatus: sequence, data: cfData);
                // v1.2.13 PATCH Item 5: increment _cfCounter before each
                // CF send so frameIndex is 1-based. If the send throws,
                // the IsoTpSendFailedException aborts the burst and
                // _sendGate.Release runs in the finally.
                _cfCounter++;
                await SendCanFrameAsync(cf.Encode(), frameIndex: _cfCounter).ConfigureAwait(false);

                offset += chunkSize;
                sequence = (sequence + 1) & 0x0F;
                cfInBlock++;

                // Apply STmin delay (inter-CF pacing, ISO 15765-2 §6.5.5.4).
                // STmin units: 0x00..0x7F → ms, 0xF1..0xF9 → 100..900 µs.
                if (offset < data.Length)
                {
                    var st = StMinToTimeSpan(_txStMin);
                    if (st > TimeSpan.Zero)
                        await Task.Delay(st, ct).ConfigureAwait(false);
                }

                // Block-Size gate: after every BS CFs (when BS>0 and more remain),
                // wait for the next FC before continuing.
                if (bs > 0 && cfInBlock >= bs && offset < data.Length)
                {
                    lock (_txLock) { _txWaitingForFc = true; }
                    if (!await WaitForFlowControlAsync(ct).ConfigureAwait(false))
                        throw new TimeoutException("No Flow Control received (block-size gate)");
                    lock (_txLock) { bs = _txBlockSize; }
                    cfInBlock = 0;
                }
            }
        }
        finally
        {
            // v1.2.14 PATCH Task 1: close the _txWaitingForFc leak introduced
            // by v1.2.13 PATCH Item 5 throw path. Previously the inner catch
            // swallowed SendCanFrameAsync failures, so _txWaitingForFc was
            // eventually cleared by the next real FC arrival in
            // HandleFlowControl. Now that Item 5 propagates IsoTpSendFailed-
            // Exception out, the finally must own the reset. Must hold
            // _txLock because the flag is also written under that lock in
            // line 367/424 (initial true + BS-gate re-true) and Reset() (line 281).
            lock (_txLock)
            {
                _txWaitingForFc = false;
            }
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Convert a raw STmin byte to a TimeSpan per ISO 15765-2 §6.5.5.4:
    /// <list type="bullet">
    /// <item>0x00..0x7F → 0..127 ms</item>
    /// <item>0x80..0xF0 → reserved, treated as 0 ms</item>
    /// <item>0xF1..0xF9 → 100..900 µs (100-µs resolution)</item>
    /// <item>0xFA..0xFF → reserved, treated as 0 ms</item>
    /// </list>
    /// </summary>
    private static TimeSpan StMinToTimeSpan(int stMinRaw)
    {
        if (stMinRaw <= 0x7F)
            return TimeSpan.FromMilliseconds(stMinRaw);
        if (stMinRaw >= 0xF1 && stMinRaw <= 0xF9)
        {
            // 100 µs = 1 tick (TimeSpan tick = 100 ns).
            return TimeSpan.FromTicks(stMinRaw - 0xF0);
        }
        return TimeSpan.Zero; // reserved range
    }

    private async Task<bool> WaitForFlowControlAsync(CancellationToken ct)
    {
        // Wait up to N_Bs for Flow Control. Default is ISO 15765-2's recommended
        // 1000 ms; overridable via FlowControlTimeout for slow ECUs.
        var timeout = _flowControlTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            lock (_txLock)
            {
                if (!_txWaitingForFc)
                    return true;
            }

            try
            {
                await Task.Delay(1, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timed out waiting for FC. Caller will throw TimeoutException.
                return false;
            }
        }

        return false;
    }
}