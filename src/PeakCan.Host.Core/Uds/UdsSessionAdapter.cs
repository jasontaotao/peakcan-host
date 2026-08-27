using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Uds;

/// <summary>
/// Wraps UdsClient to implement IUdsSession.
/// Translates all UdsException types to HIL Contracts exceptions.
/// Task B 第一步（Q1，spec 2026-08-27）：从 Infrastructure/Uds 迁入 Core——
/// Core.Tests 与 Infrastructure 均经 InternalsVisibleTo 可见，单一 adapter 实现
/// 服务两侧；DID 读写两方法随 ReadDid/WriteDid executor 接口化同步补齐。
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

    public async Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct)
    {
        try
        {
            return await _client.ReadDataByIdentifierAsync(did, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            // 不加 "ReadDID ... failed:" 前缀——executor 已拥有该短语，叠加会双重拼接（review MEDIUM）
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct)
    {
        try
        {
            await _client.WriteDataByIdentifierAsync(did, data, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            // 不加 "WriteDID ... failed:" 前缀——executor 已拥有该短语，叠加会双重拼接（review MEDIUM）
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    // ── Task B 第二步（Q1，spec 2026-08-27）：SessionControl/ClearDtc/Routine/
    // Security/ECUReset/CommCtrl/IOControl 7 类 executor 从 concrete UdsClient 迁到本接口 ──

    public async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct)
    {
        try
        {
            return await _client.DiagnosticSessionControlAsync(sessionType, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task ClearDiagnosticInformationAsync(uint groupOfDtc, CancellationToken ct)
    {
        try
        {
            await _client.ClearDiagnosticInformationAsync(groupOfDtc, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data, CancellationToken ct)
    {
        try
        {
            return await _client.RoutineControlAsync(routineControlType, routineId, data, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task<byte[]> RequestSeedAsync(byte level, CancellationToken ct)
    {
        try
        {
            return await _client.RequestSeedAsync(level, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task<byte[]> SecurityAccessAsync(byte level, CancellationToken ct)
    {
        try
        {
            return await _client.SecurityAccessAsync(level, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct)
    {
        try
        {
            return await _client.EcuResetAsync(resetType, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task TesterPresentAsync(bool suppressPosResponse, CancellationToken ct)
    {
        try
        {
            await _client.TesterPresentAsync(suppressPosResponse, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
        }
    }

    public async Task<byte[]> IOControlAsync(ushort did, byte controlType, byte[]? data, byte controlEnableMask = 0xFF, CancellationToken ct = default)
    {
        try
        {
            return await _client.IOControlAsync(did, controlType, data, controlEnableMask, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            throw new UdsSessionTransportException(ex.Message, ex);
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
