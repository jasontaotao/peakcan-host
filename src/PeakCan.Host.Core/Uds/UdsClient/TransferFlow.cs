namespace PeakCan.Host.Core.Uds;

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
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: virtual seam so end-to-end test doubles can
    /// intercept wire-level CAN frame emit via the override of
    /// <see cref="SendRequestAsync"/>. S3 keepalive tests in
    /// <c>UdsSessionTests</c> previously relied on the same seam - this
    /// method was the undeclared one they couldn't override.
    /// </remarks>
    public virtual async Task TesterPresentAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(0x3E, [0x00], ct).ConfigureAwait(false);
        Session.ResetS3Timer();
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
    public async Task<int> RequestDownloadAsync(uint address, uint length, CancellationToken ct = default)
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

        // C-7 fix: response layout per ISO 14229-1 §10.6.2.4 is
        //   [dataFormatId, lengthFormatId, maxNumberOfBlockLength (lengthFormatId.lowNibble bytes)]
        // SendRequestAsync strips the SID, so response[0] is dataFormatId,
        // response[1] is lengthFormatId, and response[2..5] are the 4-byte
        // maxNumberOfBlockLength (the common case, low nibble = 4).
        if (response.Length < 5)
            throw new UdsException(
                $"Invalid RequestDownload response: length {response.Length} < 5");

        // Parse max block length (simplified: assume 4-byte)
        int blockLength = (response[1] << 24) | (response[2] << 16) | (response[3] << 8) | response[4];
        return blockLength;
    }

    /// <summary>
    /// TransferData (0x36).
    /// </summary>
    /// <param name="blockSequenceCounter">Block sequence counter (1-255).</param>
    /// <param name="data">Data to transfer.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TransferDataAsync(byte blockSequenceCounter, byte[] data, CancellationToken ct = default)
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
    public async Task RequestTransferExitAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(0x37, null, ct).ConfigureAwait(false);
    }
}
