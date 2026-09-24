using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Capability;

/// <summary>
/// A2ML OPTIONAL_CMD 名称 → XCP 1.0 线上命令码的内置归一表（spec §3 Capability）。
/// <para>
/// 为什么不用字符串直比：A2ML 枚举存在历史别名——XCP 1.0 前身把
/// START_STOP_DAQ_LIST 称作 SET_DAQ_LIST_MODE，真机 A2L（App_merge_INCA.a2l
/// 行 765-766）两个名字都出现在 OPTIONAL_CMD 里，而其 A2ML 枚举自带的数值
///（SET_DAQ_LIST_MODE=224=0xE0、START_STOP_DAQ_LIST=222=0xDE）与线上命令码
/// **不是一套**编号——START_STOP_DAQ_LIST 的 222≡0xDE 与线上码 0xDE 相等纯属
/// 巧合（同表内 SET_DAQ_LIST_MODE=224=0xE0≠0xDE 即证），不能拿枚举数值归一。
/// 别名表按命令语义归一到线上命令码：SET_DAQ_LIST_MODE ≡ START_STOP_DAQ_LIST ≡ 0xDE。
/// </para>
/// <para>
/// 归一后的比对在 XcpCapabilityReconciler 内按字节码集合进行；
/// 表外的名字一律报告 COMMAND_NAME_UNKNOWN 告警，绝不静默跳过。
/// </para>
/// </summary>
public static class XcpA2mlCommandAlias
{
    /// <summary>A2ML 枚举名 → 线上命令码（字节值一律取 XcpPid 常量，禁止散写字面量）。</summary>
    private static readonly Dictionary<string, byte> CommandCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GET_COMM_MODE_INFO"] = XcpPid.GetCommModeInfo,
        ["SET_MTA"] = XcpPid.SetMta,
        ["UPLOAD"] = XcpPid.Upload,
        ["SHORT_UPLOAD"] = XcpPid.ShortUpload,
        ["DOWNLOAD"] = XcpPid.Download,
        ["SET_DAQ_PTR"] = XcpPid.SetDaqPtr,
        ["WRITE_DAQ"] = XcpPid.WriteDaq,
        ["CLEAR_DAQ_LIST"] = XcpPid.ClearDaqList,
        ["START_STOP_DAQ_LIST"] = XcpPid.StartStopDaqList,
        ["SET_DAQ_LIST_MODE"] = XcpPid.StartStopDaqList,   // A2ML 历史别名（XCP 1.0 旧名）
        ["START_STOP_SYNCH"] = XcpPid.StartStopSynch,
        ["GET_DAQ_PROCESSOR_INFO"] = XcpPid.GetDaqProcessorInfo,
        ["GET_DAQ_RESOLUTION_INFO"] = XcpPid.GetDaqResolutionInfo,
        ["GET_DAQ_LIST_INFO"] = XcpPid.GetDaqListInfo,
        ["GET_DAQ_EVENT_INFO"] = XcpPid.GetDaqEventInfo,
    };

    /// <summary>
    /// 归一：A2ML 命令名 → 线上命令码。别名 SET_DAQ_LIST_MODE 与
    /// START_STOP_DAQ_LIST 归一到同一码。表外名字返回 false（消费方必须告警）。
    /// </summary>
    public static bool TryGetCommandCode(string a2mlCommandName, out byte commandCode) =>
        CommandCodes.TryGetValue(a2mlCommandName, out commandCode);

    /// <summary>批量归一。返回成功归一的命令码集合 + 无法归一的原始名（消费方逐名告警）。</summary>
    public static (IReadOnlySet<byte> Codes, IReadOnlyList<string> UnknownNames) Normalize(
        IReadOnlyList<string> a2mlCommandNames)
    {
        var codes = new HashSet<byte>();
        var unknown = new List<string>();
        foreach (var name in a2mlCommandNames)
        {
            if (TryGetCommandCode(name, out var code))
                codes.Add(code);
            else
                unknown.Add(name);
        }

        return (codes, unknown);
    }
}
