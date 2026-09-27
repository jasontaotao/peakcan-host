using System.Globalization;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>C-1 广播写场景结果（spec §1.3：同值广播全元素——真机行为是否合期望由人工判定）。</summary>
public sealed record BroadcastWriteResult(
    int ElementCount,
    ReadModifyRestoreResult Rmr);

/// <summary>
/// S7-T5 C-1 场景：多元素对象广播写 + 读-改-还原全流程。元素数事实随结果出站，
/// 报告人工判定栏直接回答"整 MAP 覆盖是否合期望 → 是否立项数组值格式"。
/// </summary>
public static class BroadcastWriteScenario
{
    public static async Task<BroadcastWriteResult> RunAsync(
        XcpCalibrationWriter writer,
        XcpMaster master,
        ValueContract contract,
        A2lEditor.Core.Model.A2lDocument document,
        double testPhysical,
        string scenarioName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if (contract.DataType is null)
            throw new InvalidOperationException($"对象 '{contract.ObjectName}' 无数据类型，无法计算元素字节数");
        var elementBytes = ByteLayout.SizeOf(contract.DataType.Value);
        var elementCount = elementBytes > 0 ? contract.TotalByteLength / elementBytes : 1;
        var rmr = await ReadModifyRestore.RunAsync(
            writer, master, contract, document, testPhysical, scenarioName, ct: ct).ConfigureAwait(false);

        return new BroadcastWriteResult(elementCount, rmr);
    }
}
