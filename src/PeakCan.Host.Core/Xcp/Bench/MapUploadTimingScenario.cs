using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Replay;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>C-3 在线 MAP 上传计时结果（耗时事实 + 网格规模；DAQ 并发行为 = 台架人工观察）。</summary>
public sealed record MapUploadTimingResult(
    string ObjectName,
    int XCount,
    int YCount,
    TimeSpan Elapsed);

/// <summary>
/// S7-T5 C-3 场景：在线 MAP UPLOAD 全元素计时（XcpMapReader.ReadOnlineAsync 原样，
/// 零 DOWNLOAD 不碰写门禁）。真机与 DAQ 并发的行为由批次运行时人工观察。
/// </summary>
public static class MapUploadTimingScenario
{
    public static async Task<MapUploadTimingResult> RunAsync(
        XcpMaster master,
        ContractSet contracts,
        string objectName,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(contracts);

        var tp = timeProvider ?? TimeProvider.System;
        var t0 = tp.GetTimestamp();
        var data = await XcpMapReader.ReadOnlineAsync(master, contracts, objectName, ct).ConfigureAwait(false);
        var elapsed = tp.GetElapsedTime(t0);

        return new MapUploadTimingResult(objectName, data.Grid.GetLength(1), data.Grid.GetLength(0), elapsed);
    }
}
