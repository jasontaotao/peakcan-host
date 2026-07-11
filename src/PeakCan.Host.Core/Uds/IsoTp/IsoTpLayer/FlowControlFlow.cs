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
        SendCanFrame(canData);
    }
}