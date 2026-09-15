namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow F: FlowControl (v1.2.12 PATCH Item 2 + earlier).
    // Methods moved verbatim from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - HandleFlowControl -> _txLock + _txWaitingForFc + _txBlockSize + _txStMin (state, main)
    //   - SendFlowControl -> _config (DI, main) + SendCanFrame (Flow B, partial file)

    private void HandleFlowControl(IsoTpFrame frame)
    {
        lock (_txLock)
        {
            if (!_txWaitingForFc)
                return;

            _txBlockSize = frame.BlockSize;
            _txStMin = frame.StMin;
            _txWaitingForFc = false;
            _hasReceivedFc = true;
        }
    }

    private void SendFlowControl()
    {
        // Send Flow Control with BS=0 (unlimited), STmin=0 (no delay)
        var fc = new IsoTpFrame(
            IsoTpFrameType.FlowControl,
            sequenceOrStatus: 0, // Continue to send
            blockSize: 0,       // Unlimited
            stMin: 0);          // No delay

        var canData = fc.Encode();

        // v1.2.15 PATCH: route the FC through the async send helper so the
        // async-ctor path (the production default) actually emits it. This is
        // the RX-side counterpart of the v1.2.12 "M-6" SF fix: SendFlowControl
        // used to call the sync-only SendCanFrame and therefore sent NOTHING when
        // the layer was built with the Func<CanFrame,Task> ctor, stalling every
        // multi-frame receive until the ECU's N_Bs timeout.
        // ProcessFrame is synchronous (invoked from the CAN read handler), so the
        // send cannot be awaited here: fire-and-forget with the failure observed
        // inside SendCanFrameAsync (logged + counted, not thrown) to keep the SDK
        // read thread alive.
        if (_sendFrameAsync is not null)
        {
            _ = SendCanFrameAsync(canData, frameIndex: 0, throwOnFailure: false);
            return;
        }

        SendCanFrame(canData);
    }
}