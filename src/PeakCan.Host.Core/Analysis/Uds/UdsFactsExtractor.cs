using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// 把一个已配对的事务还原成协议对象。
/// <para>
/// 与 <see cref="UdsServiceInterpreter"/> 的分工：解释器负责「显示什么」（服务名、
/// DID 名称、解码值、降级原因）；本类只负责「操作的是什么」（DID / 例程 / 会话 /
/// 安全等级 / 块序号）。两者都读同一个 payload，但消费方不同——
/// 解释器给 UI 表格用，本类给状态机和 finding 生成器用。
/// </para>
/// </summary>
public sealed class UdsFactsExtractor
{
    private static readonly IReadOnlyDictionary<byte, string> Services = new Dictionary<byte, string>
    {
        [0x10] = "DiagnosticSessionControl",
        [0x11] = "ECUReset",
        [0x14] = "ClearDiagnosticInformation",
        [0x19] = "ReadDTCInformation",
        [0x22] = "ReadDataByIdentifier",
        [0x27] = "SecurityAccess",
        [0x28] = "CommunicationControl",
        [0x2E] = "WriteDataByIdentifier",
        [0x31] = "RoutineControl",
        [0x34] = "RequestDownload",
        [0x35] = "RequestUpload",
        [0x36] = "TransferData",
        [0x37] = "RequestTransferExit",
        [0x3D] = "WriteMemoryByAddress",
        [0x3E] = "TesterPresent",
        [0x85] = "ControlDTCSetting",
    };

    public UdsOperationFacts Extract(UdsOfflineTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var req = transaction.Request.Payload;
        if (req.Length == 0)
            return new(0, "SID 0x00", null, null, null, null, null, null, null, null, null);

        var sid = req[0];
        var name = Services.TryGetValue(sid, out var known) ? known : $"SID 0x{sid:X2}";

        byte? sessionType = null;
        byte? securityLevel = null;
        UdsSecurityStep? securityStep = null;
        ushort? did = null;
        ushort? routineId = null;
        byte? routineControlType = null;
        byte? blockSequence = null;

        switch (sid)
        {
            case 0x10 when req.Length >= 2:
                // 掩掉抑制位：10 83 请求的是 03 会话。
                sessionType = (byte)(req[1] & UdsSubFunction.SubFunctionMask);
                break;

            case 0x27 when req.Length >= 2:
                // 子功能低 7 位是等级，最高位是「抑制正响应」，不影响等级与阶段。
                var sub = (byte)(req[1] & 0x7F);
                securityStep = (sub & 1) == 1 ? UdsSecurityStep.RequestSeed : UdsSecurityStep.SendKey;
                securityLevel = (byte)((sub + 1) / 2);
                break;

            case 0x22 or 0x2E when req.Length >= 3:
                did = (ushort)((req[1] << 8) | req[2]);
                break;

            case 0x31 when req.Length >= 4:
                routineControlType = req[1];
                routineId = (ushort)((req[2] << 8) | req[3]);
                break;

            case 0x36 when req.Length >= 2:
                blockSequence = req[1];
                break;
        }

        var nrc = transaction.FinalKind == UdsFinalKind.Negative ? transaction.FinalNrc : null;
        var nrcName = nrc is { } value && Enum.IsDefined(typeof(UdsNegativeResponseCode), value)
            ? ((UdsNegativeResponseCode)value).ToString()
            : null;

        return new(sid, name, sessionType, securityLevel, securityStep,
            did, routineId, routineControlType, blockSequence, nrc, nrcName);
    }
}
