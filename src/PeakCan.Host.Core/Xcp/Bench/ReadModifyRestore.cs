using System.Globalization;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>读-改-还原单场景结果（spec D3）。</summary>
/// <param name="OriginalPhysical">保存到的原值（规划失败/未读时 NaN）。</param>
/// <param name="WriteVerified">测试值写入且回读一致（writer Written）。</param>
/// <param name="Restored">还原确认：内存回到原值，或零流量拒绝无变更。</param>
public sealed record ReadModifyRestoreResult(
    string Scenario,
    double OriginalPhysical,
    double TestPhysical,
    bool WriteVerified,
    bool Restored,
    string Detail)
{
    /// <summary>报告还原记录行（判据 3：零遗留证据）。</summary>
    public BenchRestoreRecord ToRestoreRecord() =>
        new(Scenario, Restored, Detail);
}

/// <summary>
/// 读-改-还原编排（S7-T3，spec D3 拍板）：保存原值 → 写测试值 → 回读校验 →
/// 还原原值 → 确认还原。写路径唯一入口 = <see cref="XcpCalibrationWriter"/>
/// （DOWNLOAD 红线）；读路径 = <see cref="XcpUploadReader"/>（须持序列门）。
/// writer 内部自持序列门——本编排读/写分段进出，不在持门状态下调用 writer。
/// </summary>
public static class ReadModifyRestore
{
    public static async Task<ReadModifyRestoreResult> RunAsync(
        XcpCalibrationWriter writer,
        XcpMaster master,
        ValueContract contract,
        A2lEditor.Core.Model.A2lDocument document,
        double testPhysical,
        string scenarioName,
        byte addressExtension = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);

        var runs = CalibrationRunPlanner.PlanWriteRuns(
            document, contract.Segments[0].Address, contract.TotalByteLength);
        if (runs is null || runs.Count == 0)
        {
            return new ReadModifyRestoreResult(
                scenarioName, double.NaN, testPhysical,
                WriteVerified: false, Restored: true,
                Detail: "段映射规划失败（零流量拒绝）——无变更，无需还原");
        }

        // ---- 1) 保存原值：逐 run 读拼接（reconciler 同口径），整体持门 ----
        var originalRaw = new byte[contract.TotalByteLength];
        using (var gate = await master.EnterMemorySequenceAsync(ct).ConfigureAwait(false))
        {
            foreach (var run in runs)
            {
                var chunk = await XcpUploadReader.ReadAsync(
                    master, run.PhysicalAddress, run.ByteLength, ct).ConfigureAwait(false);
                chunk.CopyTo(originalRaw, run.SourceOffset);
            }
        }

        var originalPhysical = contract.Decode(originalRaw);

        // ---- 2) 写测试值（writer 内部持门 + 回读校验）→ 3) 还原（try/finally 保证）----
        var writeOutcome = await writer.WriteAsync(
            contract, document, testPhysical, addressExtension, ct).ConfigureAwait(false);

        var writeVerified = writeOutcome.Status == CalibrationWriteStatus.Written;
        bool restored;
        var detailTail = string.Empty;

        if (writeOutcome.Status == CalibrationWriteStatus.Rejected)
        {
            // 零流量拒绝：从机未变，无需还原（也不产生还原写流量）。
            restored = true;
            detailTail = "；零流量拒绝，无需还原";
        }
        else
        {
            // 写已发生（成功或失败）——必须还原，还原失败本身 fail-loud 记录。
            var restoreOutcome = await writer.WriteAsync(
                contract, document, originalPhysical, addressExtension, ct).ConfigureAwait(false);
            restored = restoreOutcome.Status == CalibrationWriteStatus.Written;
            detailTail = restored
                ? "；原值已还原并回读确认"
                : $"；还原失败（{restoreOutcome.Status}: {restoreOutcome.Detail}）——需人工检查 ECU";
        }

        var detail = $"原值 {Format(originalPhysical)}；测试值 {Format(testPhysical)}；" +
                     $"写场景 {writeOutcome.Status}{detailTail}";

        return new ReadModifyRestoreResult(
            scenarioName, originalPhysical, testPhysical, writeVerified, restored, detail);
    }

    private static string Format(double v) =>
        v.ToString("R", CultureInfo.InvariantCulture);
}
