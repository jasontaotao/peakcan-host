namespace PeakCan.HIL.Core.Uds;

public partial class UdsClient
{
    // Flow E: TesterPresent + RoutineControl + Transfer (0x3E + 0x31 + 0x34 + 0x36 + 0x37).
    // TesterPresent (wire-emit 0x3E) + RoutineControl x 2 overloads (0x31) +
    // RequestDownload (0x34) + TransferData (0x36) + RequestTransferExit (0x37).
    // Extracted from UdsClient.cs verbatim per W12 D5.
    // Note: S3 keepalive FACADES (StartTesterPresent/StopTesterPresent) live in
    // SessionFlow (Flow B) per W12 D2 grouping principle (state-mutating session
    // ops, not wire-emit).

    /// <summary>
    /// TesterPresent (0x3E).
    /// </summary>
    /// <param name="suppressPosResponse">
    /// Phase 2: when true, send sub-function 0x80 (suppress positive response).
    /// Use during flashing to avoid the response frame interfering with the
    /// ISO-TP multi-frame download stream. Falls back to 0x00 if the ECU
    /// does not support suppression.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: virtual seam so end-to-end test doubles can
    /// intercept wire-level CAN frame emit via the override of
    /// <see cref="SendRequestAsync"/>. S3 keepalive tests in
    /// <c>UdsSessionTests</c> previously relied on the same seam - this
    /// method was the undeclared one they couldn't override.
    /// </remarks>
    public virtual async Task TesterPresentAsync(bool suppressPosResponse = false, CancellationToken ct = default)
    {
        byte subFunc = suppressPosResponse ? (byte)0x80 : (byte)0x00;
        await SendRequestAsync(0x3E, [subFunc], ct).ConfigureAwait(false);
        Session.ResetS3Timer();
    }

    /// <summary>
    /// Phase 2: CommunicationControl (0x28) — broadcast to all ECUs to enable/disable
    /// communication. Uses functional addressing (no response expected). Typical use:
    /// disable all ECU communication before flashing, re-enable after.
    /// </summary>
    /// <param name="subFunc">Sub-function: EnableRxAndTx (0x00), EnableRxDisableTx (0x01),
    /// DisableRxAndTx (0x02), etc.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual Task CommunicationControlAsync(CommunicationSubFunction subFunc, CancellationToken ct = default)
    {
        // 0x28 is a functional-address service — send via IsoTpLayer.SendFunctionalAsync
        // which uses FunctionalId and does not wait for a response.
        return _isoTp.SendFunctionalAsync([(byte)0x28, (byte)subFunc], ct);
    }

    /// <summary>
    /// Phase 2: DTCControl (0x14) — clear or read DTCs. Uses physical addressing
    /// (expects response). The response carries DTC status/data for read operations.
    /// </summary>
    /// <param name="subFunc">Sub-function (0x01=ReportDTCByStatusMask, etc.).</param>
    /// <param name="dtcDroup">3-byte DTC group (0x00FFFFFF = all DTCs for clear).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<byte[]> DtcControlAsync(DtcControlSubFunction subFunc, uint dtcGroup, CancellationToken ct = default)
    {
        var requestData = new byte[4];
        requestData[0] = (byte)subFunc;
        requestData[1] = (byte)(dtcGroup >> 16);
        requestData[2] = (byte)(dtcGroup >> 8);
        requestData[3] = (byte)(dtcGroup);
        return await SendRequestAsync(0x14, requestData, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RoutineControl (0x31).
    /// </summary>
    /// <param name="routineControlType">Type (1=Start, 2=Stop, 3=QueryResult).</param>
    /// <param name="routineId">Routine ID (2 bytes).</param>
    /// <param name="data">Optional routine data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Routine result bytes.</returns>
    public virtual async Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data = null, CancellationToken ct = default)
    {
        var requestData = new byte[3 + (data?.Length ?? 0)];
        requestData[0] = routineControlType;
        requestData[1] = (byte)(routineId >> 8);
        requestData[2] = (byte)(routineId & 0xFF);
        if (data is not null)
            Array.Copy(data, 0, requestData, 3, data.Length);

        var response = await SendRequestAsync(0x31, requestData, ct).ConfigureAwait(false);

        // Response: [routineControlType, routineIdhigh, routineIdlow, result...]
        if (response.Length < 3)
            throw new UdsException("Invalid RoutineControl response");

        return response[3..];
    }

    /// <summary>
    /// v1.3.0 MINOR Item 3/4: type-safe enum overload.
    /// </summary>
    /// <param name="routineControlType">ISO 14229-1 §10.4 standard sub-function.</param>
    /// <param name="routineId">Routine identifier (2 bytes).</param>
    /// <param name="data">Optional routine data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Routine result bytes (after the [sub, routineIdHigh, routineIdLow] prefix).</returns>
    public Task<byte[]> RoutineControlAsync(
        RoutineControlType routineControlType, ushort routineId,
        byte[]? data = null, CancellationToken ct = default)
        => RoutineControlAsync((byte)routineControlType, routineId, data, ct);

    /// <summary>
    /// RequestDownload (0x34).
    /// </summary>
    /// <param name="address">Memory address.</param>
    /// <param name="length">Data length.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Block length for TransferData.</returns>
    /// <remarks>
    /// Phase 1 C4 (flashing feature 2026-07-22): marked <c>virtual</c> so the
    /// PipelineExecutor's TransferData-chunk loop can be unit-tested against a
    /// recording UdsClient without touching the wire — consistent with the v1.2.14
    /// Item 4 / v1.3.0 Item 2 virtual-seam policy applied to the sibling Session/
    /// Security/Reset methods. The runtime body is unchanged.
    /// </remarks>
    public virtual async Task<int> RequestDownloadAsync(uint address, uint length, CancellationToken ct = default)
    {
        // Format: [dataFormatId, addressAndLengthFormatId, address..., length...]
        // Simplified: 4-byte address, 4-byte length
        var requestData = new byte[10];
        requestData[0] = 0x00; // No compression, no encryption
        requestData[1] = 0x44; // 4-byte address, 4-byte length
        requestData[2] = (byte)(address >> 24);
        requestData[3] = (byte)((address >> 16) & 0xFF);
        requestData[4] = (byte)((address >> 8) & 0xFF);
        requestData[5] = (byte)(address & 0xFF);
        requestData[6] = (byte)(length >> 24);
        requestData[7] = (byte)((length >> 16) & 0xFF);
        requestData[8] = (byte)((length >> 8) & 0xFF);
        requestData[9] = (byte)(length & 0xFF);

        var response = await SendRequestAsync(0x34, requestData, ct).ConfigureAwait(false);

        // C-1 fix: response layout per ISO 14229-1 §10.6.2.4 is
        //   [dataFormatId, lengthFormatId, maxNumberOfBlockLength (lengthFormatId.lowNibble bytes)]
        // SendRequestAsync strips the SID, so response[0] is dataFormatId,
        // response[1] is lengthFormatId. The low nibble of lengthFormatId gives
        // the byte count of maxNumberOfBlockLength; the high nibble is reserved.
        // The previous code read response[1..4] as a fixed 4-byte blockLength,
        // which incorrectly included the lengthFormatId byte (e.g. 0x44) as the
        // high byte - producing a giant blockLength and breaking TransferData chunking.
        if (response.Length < 2)
            throw new UdsException(
                $"Invalid RequestDownload response: length {response.Length} < 2 (need at least dataFormatId + lengthFormatId)");

        int blockLengthBytes = response[1] & 0x0F;
        if (blockLengthBytes == 0)
            throw new UdsException(
                $"Invalid RequestDownload response: lengthFormatId 0x{response[1]:X2} has low nibble 0 (no maxNumberOfBlockLength field)");

        if (response.Length < 2 + blockLengthBytes)
            throw new UdsException(
                $"Invalid RequestDownload response: length {response.Length} < {2 + blockLengthBytes} (lengthFormatId low nibble = {blockLengthBytes} requires {blockLengthBytes} bytes for maxNumberOfBlockLength)");

        // Parse maxNumberOfBlockLength as big-endian, variable-length per lengthFormatId.lowNibble.
        int blockLength = 0;
        for (int i = 0; i < blockLengthBytes; i++)
            blockLength = (blockLength << 8) | response[2 + i];
        return blockLength;
    }

    /// <summary>
    /// TransferData (0x36).
    /// </summary>
    /// <param name="blockSequenceCounter">Block sequence counter (1-255).</param>
    /// <param name="data">Data to transfer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Phase 1 C4 (flashing feature 2026-07-22): marked <c>virtual</c> for the
    /// PipelineExecutor chunk-counter test seam (see <see cref="RequestDownloadAsync"/>).
    /// </remarks>
    public virtual async Task TransferDataAsync(byte blockSequenceCounter, byte[] data, CancellationToken ct = default)
    {
        var requestData = new byte[1 + data.Length];
        requestData[0] = blockSequenceCounter;
        Array.Copy(data, 0, requestData, 1, data.Length);

        await SendRequestAsync(0x36, requestData, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RequestTransferExit (0x37).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Phase 1 C4 (flashing feature 2026-07-22): marked <c>virtual</c> for the
    /// PipelineExecutor test seam (see <see cref="RequestDownloadAsync"/>).
    /// </remarks>
    public virtual async Task RequestTransferExitAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(0x37, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// IOControl (0x2F) — physical addressing, waits for positive response.
    /// Data 参数是 controlParam only；executor 内部 prepend [didHi, didLo, mask]。
    /// </summary>
    /// <param name="did">Data Identifier (2 bytes, big-endian).</param>
    /// <param name="controlType">Control type (0x00 returnControlToECU, 0x01 resetToDefault,
    /// 0x02 freezeCurrentState, 0x03 shortTermAdjustment).</param>
    /// <param name="controlParam">Optional control parameter bytes (excluded from the DID header).</param>
    /// <param name="controlEnableMask">Control enable mask (default 0xFF = all bits enabled).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response bytes after the [didHi, didLo, mask] header (controlStatus onwards);
    /// empty when the response is shorter than the 3-byte header.</returns>
    /// <remarks>
    /// ODX Phase 0 (Task 0.2): marked <c>virtual</c> per the established virtual-seam policy
    /// (see <see cref="SendRequestAsync"/> / <see cref="RequestDownloadAsync"/>) so test doubles
    /// can intercept wire emit without subclassing the transport.
    /// </remarks>
    public virtual async Task<byte[]> IOControlAsync(
        ushort did, byte controlType, byte[]? controlParam = null,
        byte controlEnableMask = 0xFF, CancellationToken ct = default)
    {
        var requestData = new byte[3 + (controlParam?.Length ?? 0)];
        requestData[0] = (byte)(did >> 8);
        requestData[1] = (byte)(did & 0xFF);
        requestData[2] = controlEnableMask;
        if (controlParam is not null)
            Array.Copy(controlParam, 0, requestData, 3, controlParam.Length);

        var response = await SendRequestAsync(0x2F, requestData, ct).ConfigureAwait(false);
        // Response: [didHi, didLo, mask, controlStatus..., controlParam...] — 返回 controlStatus 起
        return response.Length >= 3 ? response[3..] : Array.Empty<byte>();
    }
}
