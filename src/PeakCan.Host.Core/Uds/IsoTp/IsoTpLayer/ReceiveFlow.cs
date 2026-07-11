namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow D: Receive (v1.2.12 PATCH Items 3/8).
    // 5 methods moved verbatim from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ProcessFrame <- public entry (caller invokes from CAN receive handler)
    //   - HandleSingleFrame <- ProcessFrame (intra-flow)
    //   - HandleFirstFrame -> LogIsoTpFfLengthTooLarge (Flow G, cross-partial)
    //                       -> CancelReceiveWatchdog (Flow E, cross-partial)
    //                       -> SendFlowControl (Flow F, cross-partial)
    //                       -> StartReceiveWatchdog (Flow E, cross-partial)
    //   - HandleConsecutiveFrame -> HandleConsecutiveFrameLocked (intra-flow)
    //                            -> LogIsoTpHandlerFailed (Flow G, cross-partial)
    //   - HandleConsecutiveFrameLocked -> CancelReceiveWatchdog (Flow E, cross-partial)
    //                                  -> StartReceiveWatchdog (Flow E, cross-partial)
    //
    // R4 (W8 lesson extension to W9): 7 cross-partial callers validated here.

    /// <summary>
    /// Process an incoming CAN frame. Call this from your CAN receive handler.
    /// </summary>
    /// <param name="frame">Received CAN frame.</param>
    public void ProcessFrame(CanFrame frame)
    {
        // Only process frames with our response CAN ID.
        if (frame.Id.Raw != _config.ResponseId)
            return;

        var isoFrame = IsoTpFrame.Decode(frame.Data.Span);

        switch (isoFrame.Type)
        {
            case IsoTpFrameType.Single:
                HandleSingleFrame(isoFrame);
                break;

            case IsoTpFrameType.First:
                HandleFirstFrame(isoFrame);
                break;

            case IsoTpFrameType.Consecutive:
                HandleConsecutiveFrame(isoFrame);
                break;

            case IsoTpFrameType.FlowControl:
                HandleFlowControl(isoFrame);
                break;
        }
    }

    private void HandleSingleFrame(IsoTpFrame frame)
    {
        // Single frame: complete message
        MessageReceived?.Invoke(frame.Data.ToArray());
    }

    private void HandleFirstFrame(IsoTpFrame frame)
    {
        // v1.2.12 PATCH Item 8: refuse FFs declaring more than MaxMessageLength
        // bytes BEFORE allocating a 4 KB+ buffer. A malicious / fuzz ECU can
        // otherwise drive the host into OOM by streaming crafted FFs. The
        // Encode() method already caps the FF length field at 12 bits (4095),
        // so this check is defense-in-depth for any future encoder change or
        // for IsoTpFrame objects constructed directly via the public ctor.
        if (frame.Length > MaxMessageLength)
        {
            if (_logger is not null)
                LogIsoTpFfLengthTooLarge(_logger, frame.Length, MaxMessageLength);
            // Reset state so a subsequent valid FF can be reassembled.
            lock (_rxLock)
            {
                _rxInProgress = false;
                _rxBuffer = null;
                _rxExpectedLength = 0;
                _rxReceivedLength = 0;
                _rxExpectedSequence = 1;
            }
            CancelReceiveWatchdog();
            return; // drop, do not throw — keep the SDK read thread alive
        }

        lock (_rxLock)
        {
            _rxInProgress = true;
            _rxExpectedLength = frame.Length;
            _rxReceivedLength = 0;
            _rxExpectedSequence = 1;
            _rxBuffer = new byte[frame.Length];

            // Copy first chunk
            var firstChunk = frame.Data.Span;
            firstChunk.CopyTo(_rxBuffer.AsSpan(0, Math.Min(firstChunk.Length, frame.Length)));
            _rxReceivedLength += firstChunk.Length;

            // Send Flow Control
            SendFlowControl();

            // Start the N_Cr watchdog: if the next CF doesn't arrive in time,
            // abort reassembly so a fresh FF can be processed.
            StartReceiveWatchdog(expectedGeneration: 1);
        }
    }

    private void HandleConsecutiveFrame(IsoTpFrame frame)
    {
        // v1.2.12 PATCH Item 3: do all state mutation under _rxLock and
        // return the reassembled message (if any) to the caller. The
        // MessageReceived handler is then invoked OUTSIDE the lock, wrapped
        // in try/catch, so a buggy subscriber cannot corrupt ISO-TP
        // reassembly state nor propagate exceptions onto the SDK read
        // thread.
        byte[]? complete = HandleConsecutiveFrameLocked(frame);
        if (complete is null)
            return;

        try
        {
            MessageReceived?.Invoke(complete);
        }
        catch (Exception ex)
        {
            // Single source of truth for the "handler threw" event (id 3002).
            if (_logger is not null)
                LogIsoTpHandlerFailed(_logger, ex, complete.Length);
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 3: lock-protected half of
    /// <see cref="HandleConsecutiveFrame"/>. Performs the sequence check
    /// and copies the new CF chunk into the reassembly buffer; if the
    /// message is complete, transfers ownership of the buffer to the
    /// caller (clearing <c>_rxBuffer</c> / <c>_rxInProgress</c>) and
    /// returns the assembled byte array. The lock is held for the
    /// duration of this method; the returned buffer is intended to be
    /// consumed AFTER the caller's lock scope ends.
    /// </summary>
    private byte[]? HandleConsecutiveFrameLocked(IsoTpFrame frame)
    {
        lock (_rxLock)
        {
            if (!_rxInProgress || _rxBuffer is null)
                return null;

            // Validate sequence number
            if (frame.SequenceOrStatus != _rxExpectedSequence)
            {
                // Sequence error: abort reassembly
                _rxInProgress = false;
                _rxBuffer = null;
                CancelReceiveWatchdog();
                return null;
            }

            // Copy data
            int remaining = _rxExpectedLength - _rxReceivedLength;
            int chunkSize = Math.Min(frame.Data.Length, remaining);
            frame.Data.Span.Slice(0, chunkSize).CopyTo(_rxBuffer.AsSpan(_rxReceivedLength, chunkSize));
            _rxReceivedLength += chunkSize;
            _rxExpectedSequence = (_rxExpectedSequence + 1) & 0x0F;

            // Check if complete
            if (_rxReceivedLength >= _rxExpectedLength)
            {
                var complete = _rxBuffer;
                _rxInProgress = false;
                _rxBuffer = null;
                CancelReceiveWatchdog();
                return complete;
            }

            // Re-arm the N_Cr watchdog for the next CF slot.
            StartReceiveWatchdog(expectedGeneration: _rxExpectedSequence);
            return null;
        }
    }
}