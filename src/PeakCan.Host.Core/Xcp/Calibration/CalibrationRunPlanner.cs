using A2lEditor.Core.IfData;
using A2lEditor.Core.Model;

namespace PeakCan.Host.Core.Xcp.Calibration;

/// <summary>
/// 对象原始数据 blob 的一个连续写入 run（S6-T6）：逻辑区间被 MEMORY_SEGMENT
/// ADDRESS_MAPPING 切出的连续一段。<see cref="SourceOffset"/> 是本 run 在对象
/// blob 内的字节偏移（与包侧 ValueSegment.SourceOffset 同语义）。
/// </summary>
public sealed record CalibrationWriteRun(
    ulong LogicalAddress,
    uint PhysicalAddress,
    int ByteLength,
    int SourceOffset);

/// <summary>
/// S6-T6 写 run 规划（spec D6）：把对象逻辑区间 [logical, logical+totalBytes)
/// 按 ADDRESS_MAPPING 覆盖切分为连续 run 序列，每 run 各自携带物理地址。
/// <para>
/// 返回 null = 规划失败（宁可不写不错写，§4.8）：起点未覆盖 / 中途出现映射空洞 /
/// 覆盖映射重叠歧义 / 覆盖段声明 ADDRESS_EXTENSION ≠ 0（S2 只建模 0，T19 口径）。
/// </para>
/// <para>
/// 单映射覆盖全程时退化为一个 run——即 S5 原分片路径（回归钉）。
/// </para>
/// </summary>
public static class CalibrationRunPlanner
{
    public static IReadOnlyList<CalibrationWriteRun>? PlanWriteRuns(
        A2lDocument document, ulong logicalAddress, int totalBytes)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (totalBytes <= 0)
            return null;

        var runs = new List<CalibrationWriteRun>();
        var logical = logicalAddress;
        var remaining = totalBytes;
        var sourceOffset = 0;
        while (remaining > 0)
        {
            XcpAddressMapping? covering = null;
            XcpSegment? owner = null;
            foreach (var memorySegment in document.Modules.SelectMany(m => m.MemorySegmentList))
            foreach (var segment in memorySegment.IfDataXcp?.Segments ?? Array.Empty<XcpSegment>())
            foreach (var map in segment.Mappings)
            {
                if (map.Length == 0 || logical < map.LogicalAddress ||
                    logical - map.LogicalAddress >= map.Length)
                    continue;
                // 重叠歧义：两个映射同时覆盖同一点 → 拒绝规划（宁可不写）。
                if (covering is not null)
                    return null;
                covering = map;
                owner = segment;
            }

            if (covering is null || owner is null)
                return null;
            // S2 只建模 addrExt 0（T19 台架证据）；其余拒绝（AcquisitionPlanner 同口径）。
            if (owner.AddressExtension is { } ext && ext != 0)
                return null;
            // 覆盖映射溢出防护与包侧 XcpAddressMap.Covers 同口径。
            if (covering.LogicalAddress > ulong.MaxValue - covering.Length ||
                covering.PhysicalAddress > ulong.MaxValue - covering.Length)
                return null;

            var runLength = (int)Math.Min(
                covering.LogicalAddress + covering.Length - logical,
                (ulong)remaining);
            if (runLength <= 0)
                return null;

            runs.Add(new CalibrationWriteRun(
                logical,
                (uint)(covering.PhysicalAddress + (logical - covering.LogicalAddress)),
                runLength,
                sourceOffset));
            logical += (ulong)runLength;
            remaining -= runLength;
            sourceOffset += runLength;
        }

        return runs;
    }
}
