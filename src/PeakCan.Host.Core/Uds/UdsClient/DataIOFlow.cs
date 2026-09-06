namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow C: DataIO + DTC (0x22 + 0x2E + 0x19 + 0x14).
    // ReadDataByIdentifier / WriteDataByIdentifier (DID round-trips) +
    // ReadDtcInformation / ClearDiagnosticInformation.
    // Extracted from UdsClient.cs verbatim per W12 D5.

    /// <summary>
    /// ReadDataByIdentifier (0x22).
    /// </summary>
    /// <param name="did">Data Identifier (2 bytes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DID data bytes.</returns>
    public virtual async Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct = default)
    {
        var didBytes = new byte[] { (byte)(did >> 8), (byte)(did & 0xFF) };
        var response = await SendRequestAsync(0x22, didBytes, ct).ConfigureAwait(false);

        // Response: [DIDhigh, DIDlow, data...]
        if (response.Length < 3)
            throw new UdsException("Invalid ReadDataByIdentifier response");

        return response[2..];
    }

    /// <summary>
    /// WriteDataByIdentifier (0x2E).
    /// </summary>
    /// <param name="did">Data Identifier (2 bytes).</param>
    /// <param name="data">Data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct = default)
    {
        var request = new byte[2 + data.Length];
        request[0] = (byte)(did >> 8);
        request[1] = (byte)(did & 0xFF);
        Array.Copy(data, 0, request, 2, data.Length);

        await SendRequestAsync(0x2E, request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ReadDTCInformation (0x19).
    /// </summary>
    /// <param name="subFunction">Sub-function (e.g., 0x02 = ReadDTCByStatusMask).</param>
    /// <param name="mask">DTC status mask.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DTC data bytes.</returns>
    public virtual async Task<byte[]> ReadDtcInformationAsync(byte subFunction, byte mask = 0xFF, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x19, [subFunction, mask], ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// ClearDiagnosticInformation (0x14).
    /// </summary>
    /// <param name="groupOfDtc">DTC group (0xFFFFFF = all).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task ClearDiagnosticInformationAsync(uint groupOfDtc = 0xFFFFFF, CancellationToken ct = default)
    {
        var requestData = new byte[3];
        requestData[0] = (byte)(groupOfDtc >> 16);
        requestData[1] = (byte)((groupOfDtc >> 8) & 0xFF);
        requestData[2] = (byte)(groupOfDtc & 0xFF);

        await SendRequestAsync(0x14, requestData, ct).ConfigureAwait(false);
    }
}
