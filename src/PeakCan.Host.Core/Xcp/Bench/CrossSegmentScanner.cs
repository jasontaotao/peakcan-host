using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>C-2 跨段对象发现（静态可算，零线上流量）。</summary>
public sealed record CrossSegmentFinding(
    string ObjectName,
    int RunCount,
    string Detail);

/// <summary>C-2 静态扫描报告（spec §1.3：真机 A2L 是否存在"单 ValueSegment 跨多 ADDRESS_MAPPING"）。</summary>
public sealed record CrossSegmentScanReport(
    int ObjectsScanned,
    int SingleRunObjects,
    int CrossSegmentObjects,
    int UnmappedObjects,
    IReadOnlyList<CrossSegmentFinding> Findings)
{
    /// <summary>C-2 结论：是否存在跨段对象（决定 RunPlanner 真机路径是否会触发）。</summary>
    public bool HasCrossSegmentObjects => CrossSegmentObjects > 0;
}

/// <summary>
/// C-2 静态跨段扫描器（S7-T5）：逐 CHARACTERISTIC 合同跑 S6
/// <c>CalibrationRunPlanner.PlanWriteRuns</c>——runs.Count &gt; 1 即跨段对象；
/// 规划 null = 映射覆盖不到（同样是台架事实：该对象在真机上不可写）。
/// 只扫写对象（CHARACTERISTIC）；MEASUREMENT/AXIS_PTS 不走写路径不进扫描。
/// </summary>
public static class CrossSegmentScanner
{
    public static CrossSegmentScanReport Scan(A2lDocument document, ContractSet contracts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(contracts);

        var findings = new List<CrossSegmentFinding>();
        var singleRun = 0;
        var unmapped = 0;
        var scanned = 0;

        foreach (var ch in document.Modules.SelectMany(m => m.Characteristics))
        {
            if (!contracts.TryGet(ch.Name, out var contract))
                continue;
            scanned++;

            var runs = Calibration.CalibrationRunPlanner.PlanWriteRuns(
                document, contract.Segments[0].Address, contract.TotalByteLength);
            if (runs is null)
            {
                unmapped++;
                findings.Add(new CrossSegmentFinding(
                    ch.Name, 0, "段映射规划失败（覆盖不到/重叠/地址扩展非 0）——真机上不可写"));
            }
            else if (runs.Count > 1)
            {
                findings.Add(new CrossSegmentFinding(
                    ch.Name, runs.Count,
                    $"逻辑区间跨 {runs.Count} 个 ADDRESS_MAPPING（物理地址不连续），写路径将拆 {runs.Count} 段执行"));
                singleRun += 0;
            }
            else
            {
                singleRun++;
            }
        }

        var ordered = findings
            .OrderByDescending(f => f.RunCount)
            .ThenBy(f => f.ObjectName, StringComparer.Ordinal)
            .ToList();
        var cross = ordered.Count(f => f.RunCount > 1);

        return new CrossSegmentScanReport(scanned, singleRun, cross, unmapped, ordered);
    }
}
