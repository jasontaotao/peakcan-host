using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.Host.Infrastructure.Uds;

/// <summary>
/// Wraps UdsClient to implement IUdsSession.
/// Translates all UdsException types to HIL Contracts exceptions.
/// </summary>
internal sealed class UdsSessionAdapter : IUdsSession
{
    private readonly UdsClient _client;

    public UdsSessionAdapter(UdsClient client) => _client = client;

    public async Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct)
    {
        try
        {
            // Service 0x19, sub-function 0x02 (reportDTCByStatusMask)
            var response = await _client.ReadDtcInformationAsync(0x02, statusMask, ct);
            return ParseDtcInfos(response);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(0x19, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException($"ReadDTC failed: {ex.Message}", ex);
        }
    }

    public async Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct)
    {
        try
        {
            await _client.SendRequestAsync(serviceId, data, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException($"SendRequest failed: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<DtcInfo> ParseDtcInfos(byte[] response)
    {
        var result = new List<DtcInfo>();
        if (response.Length < 5) return result;

        for (int i = 1; i + 3 < response.Length; i += 4)
        {
            ushort code = (ushort)((response[i] << 8) | response[i + 1]);
            byte status = response[i + 3];
            result.Add(new DtcInfo(code, status));
        }
        return result;
    }
}
