using System.Globalization;
using PeakCan.Host.Core.Xcp.Capability;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>
/// 能力探针结果 → A 系验证矩阵项映射（spec §1.1：A-1/2/3/4/5/10）。
/// 纯函数：事实只来自 <see cref="XcpCapabilityProbeResult"/> 与传入 CAN ID，
/// 查询失败项不冒充实测（探针 L2 归因口径的下游延续）。
/// A-11 位域统计由 T5 静态扫描交付（A2L 输入，非能力链）；A-3 抖动由 B-1 并发场景计时。
/// </summary>
public static class CapabilityBenchItems
{
    /// <summary>XCP 29 位扩展 CAN ID 合规上限（0x1FFFFFFF）。</summary>
    private const uint MaxExtendedCanId = 0x1FFF_FFFF;

    public static IReadOnlyList<BenchItem> FromProbeResult(
        XcpCapabilityProbeResult probe,
        uint masterCanIdRaw,
        uint slaveCanIdRaw)
    {
        ArgumentNullException.ThrowIfNull(probe);

        // A-1：资源位图 + 实测支持命令数（良性探测含 DOWNLOAD，S3-D7 口径）。
        var a1 = new BenchItem("A-1", BenchItemStatus.Measured, "从机能力清单（CONNECT + OPTIONAL_CMD 实测）",
            Facts: new Dictionary<string, string>
            {
                ["resourceBitmap"] = $"0x{probe.Connect.Resources:X2}",
                ["measuredCommandCount"] = probe.Measured.OptionalCommands.Count.ToString(CultureInfo.InvariantCulture),
                ["queryFailureCount"] = probe.QueryFailures.Count.ToString(CultureInfo.InvariantCulture),
            });

        // A-2：事件节拍。探针单项查询失败时结果字段是类型默认值——标 NotCollected 不冒充实测
        //（真机从机 GET_DAQ_EVENT_INFO 有已钉死布局偏差，失败是现实路径）。
        var a2 = Failed(probe, "GET_DAQ_EVENT_INFO")
            ? NotCollected("A-2", "事件节拍查询失败（探针归因见能力探针清单）——不冒充实测")
            : new BenchItem("A-2", BenchItemStatus.Measured, "事件节拍（GET_DAQ_EVENT_INFO）",
            Facts: new Dictionary<string, string>
            {
                ["eventChannelCount"] = probe.EventInfo.EventChannel.ToString(CultureInfo.InvariantCulture),
                ["maxDaqList"] = probe.EventInfo.MaxDaqList.ToString(CultureInfo.InvariantCulture),
                ["priority"] = probe.EventInfo.Priority.ToString(CultureInfo.InvariantCulture),
            });

        // A-4：CAN 号合规（双 ID + 29 位判定；探针占位的台架回填即本条）。
        var a4 = new BenchItem("A-4", BenchItemStatus.Measured, "CAN 号合规性",
            Facts: new Dictionary<string, string>
            {
                ["masterCanId"] = $"0x{masterCanIdRaw:X}",
                ["slaveCanId"] = $"0x{slaveCanIdRaw:X}",
                ["canId29BitCompliant"] = (masterCanIdRaw <= MaxExtendedCanId
                    && slaveCanIdRaw <= MaxExtendedCanId).ToString().ToLowerInvariant(),
            });

        // A-5：ODT 打包上限（同 P1-2 口径）。
        var a5 = Failed(probe, "GET_DAQ_LIST_INFO")
            ? NotCollected("A-5", "ODT 上限查询失败（探针归因见能力探针清单）——不冒充实测")
            : new BenchItem("A-5", BenchItemStatus.Measured, "ODT 打包上限（GET_DAQ_LIST_INFO）",
            Facts: new Dictionary<string, string>
            {
                ["maxOdt"] = probe.ListInfo.MaxOdt.ToString(CultureInfo.InvariantCulture),
                ["maxDaq"] = probe.ListInfo.MaxDaqList.ToString(CultureInfo.InvariantCulture),
                ["firstPid"] = probe.ListInfo.FirstPid.ToString(CultureInfo.InvariantCulture),
            });

        // A-10：块模式位图（GET_COMM_MODE_INFO 实测为准——S5 附录 C-2/A-10 延伸钉；同 P1-2 口径）。
        var a10 = Failed(probe, "GET_COMM_MODE_INFO")
            ? NotCollected("A-10", "COMM_MODE 查询失败（探针归因见能力探针清单）——不冒充实测")
            : new BenchItem("A-10", BenchItemStatus.Measured, "块模式能力（GET_COMM_MODE_INFO 实测位图）",
            Facts: new Dictionary<string, string>
            {
                ["commModeOptionalBitmap"] = $"0x{probe.CommMode.CommModeOptional:X2}",
                ["masterBlockMode"] = ((probe.CommMode.CommModeOptional & 0x01) != 0).ToString().ToLowerInvariant(),
            });

        return [a1, a2, a4, a5, a10];
    }

    private static bool Failed(XcpCapabilityProbeResult probe, string command) =>
        probe.QueryFailures.Any(f => f.Command == command);

    private static BenchItem NotCollected(string itemId, string summary) =>
        new(itemId, BenchItemStatus.NotCollected, summary);
}
