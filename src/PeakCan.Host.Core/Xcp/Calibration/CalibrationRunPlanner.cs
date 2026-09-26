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
/// 覆盖映射重叠（含 run 内部部分重叠，T7 评审 P1-2）/ ADDRESS_EXTENSION 非 0 或缺失
/// （S2 只建模 0，T19 口径，AcquisitionPlanner R3 同款）/ 物理地址超出 uint 寻址（T7 评审 P1-3）。
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

        var allMappings = document.Modules
            .SelectMany(m => m.MemorySegmentList)
            .SelectMany(ms => ms.IfDataXcp?.Segments ?? Array.Empty<XcpSegment>())
            .SelectMany(seg => seg.Mappings, (seg, map) => (seg, map))
            .Where(t => t.map.Length > 0)
            .ToArray();

        var runs = new List<CalibrationWriteRun>();
        var logical = logicalAddress;
        var remaining = totalBytes;
        var sourceOffset = 0;
        while (remaining > 0)
        {
            // S2/T19 口径（与 AcquisitionPlanner R3 一致）：addrExt 非 0 或缺失
            // （未声明语义）一律拒绝——null 放行等于对 addrExt 语义静默兜底。
            var candidates = allMappings.Where(t =>
                logical >= t.map.LogicalAddress &&
                logical - t.map.LogicalAddress < t.map.Length).ToArray();
            if (candidates.Length != 1)
                return null; // 覆盖不到或重叠歧义（宁可不写不错写）
            var (owner, covering) = candidates[0];
            if (owner.AddressExtension is not { } ext || ext != 0)
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

            // T7 评审 P1-2：run 内部重叠检测——本 run 的整个逻辑区间必须只被
            // 唯一映射覆盖（重复声明的同一三元组视为同一映射），否则拒绝。
            var runEnd = logical + (ulong)runLength;
            foreach (var t in allMappings)
            {
                var overlaps = logical < t.map.LogicalAddress + t.map.Length &&
                               t.map.LogicalAddress < runEnd;
                var same = t.map.LogicalAddress == covering.LogicalAddress &&
                           t.map.PhysicalAddress == covering.PhysicalAddress &&
                           t.map.Length == covering.Length;
                if (overlaps && !same)
                    return null;
            }

            // T7 评审 P1-3：物理地址必须落在 uint 寻址内——S2 写/读原语用 uint，
            // 静默截断会把数据写到回绕后的错误地址（宁可不写不错写）。
            var physical = covering.PhysicalAddress + (logical - covering.LogicalAddress);
            if (physical > uint.MaxValue)
                return null;

            runs.Add(new CalibrationWriteRun(logical, (uint)physical, runLength, sourceOffset));
            logical = runEnd;
            remaining -= runLength;
            sourceOffset += runLength;
        }

        return runs;
    }
}
