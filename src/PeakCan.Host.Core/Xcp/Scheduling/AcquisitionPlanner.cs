using System;
using System.Collections.Generic;
using System.Linq;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>
/// S2-T9 采集规划器（spec §3 Scheduling / §1 从机硬约束）：
/// <list type="bullet">
/// <item>7B 装箱：按尺寸类<b>同类同箱</b>（定案，T9 review F5）——4B 条 1 条/ODT、
/// 2B 条 3 条/ODT、1B 条 7 条/ODT；spec §3 括号枚举按齐次填充口径执行，混装放弃
/// （收益不足且破坏反查判别）。单 DAQ 表 15 ODT、ODT 数据场 7B（DTO 8B − PID 1B）
/// 是硬上限，MAX_ODT_ENTRIES=100 只是声明天花板，绝不参与容量计算。</item>
/// <item>溢出降级：15×7=105 B/拍是容量上限，但降级判据是 <b>ODT 预算</b>（15 个），
/// 不是字节数（T9 review F3 判别性测试钉死）；预算耗尽后余量自动降级轮询。</item>
/// <item>位域/非字节对齐量与 &gt;条目上限量直接归轮询（DAQ 装不下；真机 BIT_MASK
/// 全 0、包契约未建位域字段——spec §5.6，非整类字节量是包侧能表达的同一形态）。</item>
/// <item>条目上限消费包侧声明值 XcpDaq.MaxOdtEntrySizeDaq（T9 review F2，对账前置）：
/// 缺声明 fail-loud 拒绝规划（宁可不采不错采），声明值只允许收紧 spec 硬上限 4B。</item>
/// <item>PID 区守卫（T9 review F1）：FIRST_PID 起 15 个 ODT 的 PID 必须落在 DTO PID
/// 区（0x00–0xFB）内，不得触 0xFF/0xFE 响应区——接收线程按 DTO 首字节分流（§3 Receive）。</item>
/// <item>[H2] 占位索引替换：AcquisitionPlan 的 (PID/ODT/Entry) 是解析期占位
/// （ODT 恒 0、FIRST_PID 起文档序），本规划器只拿它的对象→地址清单，输出方案
/// 由装箱自建 <see cref="PlannedAcquisitionMap"/>，占位编号整体弃用。</item>
/// <item>确定性：无随机源、按文档序 + 固定尺寸类顺序装箱，同输入两次规划逐字节一致。</item>
/// <item>地址翻译：物理地址一律走包侧 <c>XcpAddressMap.TryTranslate</c> 唯一入口，
/// 本规划器不做任何地址算术。</item>
/// </list>
/// </summary>
public static class AcquisitionPlanner
{
    /// <summary>单 DAQ 表 ODT 数（spec §1：MAX_DAQ=1、15 ODT）。</summary>
    public const int MaxOdts = 15;

    /// <summary>ODT 数据场字节数（DTO 8B − PID 1B；无从机时间戳、无 PID_OFF）。</summary>
    public const int OdtDataFieldBytes = 7;

    /// <summary>单条目字节 spec 硬上限（MAX_ODT_ENTRY_SIZE_DAQ 实测基线；实际取 min(声明值, 本值)）。</summary>
    public const int MaxEntryBytes = 4;

    // 装箱尺寸类与填充顺序（固定，保证确定性）：4B 先占箱、再 2B、最后 1B。
    private static readonly int[] SizeClasses = [4, 2, 1];

    /// <summary>装箱候选：一个对象的一个段（≤条目上限且落进尺寸类才有资格进 DAQ）。</summary>
    private sealed record Candidate(string ObjectName, int SegmentIndex, int ByteLength, int SourceOffset, ulong Address);

    /// <summary>
    /// 规划采集方案。输入 = 包侧合同（逐对象 Suitability/Notes）+ 占位计划
    /// （对象→地址清单）；输出 = 自建打包方案 + 轮询降级集合。
    /// </summary>
    public static PlannedAcquisitionMap Plan(ContractSet contracts, AcquisitionPlan placeholders)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(placeholders);

        var doc = contracts.Document;
        if (doc.Modules.Count != 1)
        {
            // 单 DAQ 表口径（spec §1）：FIRST_PID/DAQ_LIST 均按单模块建模，
            // 多模块文档的占位编号本身就会碰撞（AcquisitionPlan.Build 同口径抛出）。
            throw new InvalidOperationException(
                $"AcquisitionPlanner supports single-module documents (spec §1: MAX_DAQ=1); got {doc.Modules.Count} modules.");
        }

        var module = doc.Modules[0];
        var (firstPid, maxEntryBytes) = ResolveDaqConfig(module);

        // 对象级候选：占位计划 All 按文档序给出对象→地址清单（每段一条），
        // 同对象多段只取首个片段定位对象名，段明细回查合同——顺序即文档序，保证确定性。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var polling = new List<PlannedPollingEntry>();
        var candidates = new Dictionary<int, List<Candidate>>();
        foreach (var fragment in placeholders.All)
        {
            if (!seen.Add(fragment.ObjectName))
                continue;
            if (!contracts.TryGet(fragment.ObjectName, out var contract) || contract.Segments.Count == 0)
                continue;

            Classify(doc, maxEntryBytes, contract, polling, candidates);
        }

        // 装箱：逐尺寸类填 ODT（同类同箱），ODT 预算 15 耗尽后余量降级轮询
        //（判据是 ODT 预算，不是 105 B 字节数——T9 review F3 判别性测试钉死）。
        var odts = new List<PlannedOdt>();
        ushort odtIndex = 0;
        foreach (var size in SizeClasses)
        {
            if (size > maxEntryBytes)
                continue; // 声明值收紧后整类出局（候选分类期已全部降级轮询）。

            var items = candidates.TryGetValue(size, out var list)
                ? list
                : new List<Candidate>();
            var cursor = 0;
            while (cursor < items.Count)
            {
                if (odtIndex >= MaxOdts)
                {
                    DowngradeLeftovers(doc, items, cursor, PlannedPollingCause.DaqCapacityExceeded, polling);
                    break;
                }

                // 该尺寸类每 ODT 条数 = 7B / 类宽（4→1、2→3、1→7），同类同箱。
                var perOdt = OdtDataFieldBytes / size;
                var entries = new List<PlannedDaqEntry>(perOdt);
                ushort entryIndex = 0;
                var offset = 0;
                while (entryIndex < perOdt && cursor < items.Count)
                {
                    var item = items[cursor];
                    // S2-T11 增量：DAQ 条目补 ECU 地址（WRITE_DAQ 需要）；物理地址
                    // 与轮询条目同走 Translate 唯一入口，不改 ODT 分配与轮转分批。
                    entries.Add(new PlannedDaqEntry(
                        (uint)(firstPid + odtIndex), odtIndex, entryIndex,
                        item.ObjectName, item.SegmentIndex, item.ByteLength, offset,
                        item.Address, Translate(doc, item.Address)));
                    offset += item.ByteLength;
                    entryIndex++;
                    cursor++;
                }

                odts.Add(new PlannedOdt(odtIndex, (uint)(firstPid + odtIndex), size, entries));
                odtIndex++;
            }
        }

        return new PlannedAcquisitionMap(daqNumber: 0, (ushort)odtIndex, odts, polling);
    }

    /// <summary>
    /// 对象分类：&gt;条目上限 → 轮询（ObjectTooLarge）；总长不是 1/2/4 整类字节 → 轮询
    ///（NotByteAlignedClass，位域/非字节对齐同口径）；合格对象逐段进装箱候选
    ///（≤4B 对象单段，防御性校验段宽也必须落进尺寸类）。
    /// </summary>
    private static void Classify(
        A2lDocument doc,
        int maxEntryBytes,
        ValueContract contract,
        List<PlannedPollingEntry> polling,
        Dictionary<int, List<Candidate>> candidates)
    {
        if (contract.TotalByteLength > maxEntryBytes)
        {
            PollEverything(doc, contract, PlannedPollingCause.ObjectTooLarge, polling);
            return;
        }

        if (!IsPackableSize(contract.TotalByteLength, maxEntryBytes))
        {
            PollEverything(doc, contract, PlannedPollingCause.NotByteAlignedClass, polling);
            return;
        }

        for (var i = 0; i < contract.Segments.Count; i++)
        {
            var segment = contract.Segments[i];
            if (!IsPackableSize(segment.ByteLength, maxEntryBytes))
            {
                // 病态切分（≤条目上限对象拆出非整类段）不猜：整对象降级轮询。
                PollEverything(doc, contract, PlannedPollingCause.NotByteAlignedClass, polling);
                return;
            }

            if (!candidates.TryGetValue(segment.ByteLength, out var list))
                candidates[segment.ByteLength] = list = new List<Candidate>();
            list.Add(new Candidate(contract.ObjectName, i, segment.ByteLength, segment.SourceOffset, segment.Address));
        }
    }

    /// <summary>尺寸类资格：落在固定类集合内且 ≤ 声明收紧后的条目上限。</summary>
    private static bool IsPackableSize(int size, int maxEntryBytes) =>
        size <= maxEntryBytes && SizeClasses.Contains(size);

    /// <summary>对象全部段降级轮询；物理地址走包侧 XcpAddressMap.TryTranslate 唯一入口。</summary>
    private static void PollEverything(
        A2lDocument doc, ValueContract contract, PlannedPollingCause cause, List<PlannedPollingEntry> polling)
    {
        for (var i = 0; i < contract.Segments.Count; i++)
        {
            var segment = contract.Segments[i];
            polling.Add(new PlannedPollingEntry(
                contract.ObjectName, i, segment.ByteLength, segment.SourceOffset,
                segment.Address, Translate(doc, segment.Address), cause));
        }
    }

    /// <summary>装箱预算耗尽后的余量降级（保持文档序，归因 DaqCapacityExceeded）。</summary>
    private static void DowngradeLeftovers(
        A2lDocument doc, List<Candidate> items, int from,
        PlannedPollingCause cause, List<PlannedPollingEntry> polling)
    {
        foreach (var item in items.Skip(from))
        {
            polling.Add(new PlannedPollingEntry(
                item.ObjectName, item.SegmentIndex, item.ByteLength, item.SourceOffset,
                item.Address, Translate(doc, item.Address), cause));
        }
    }

    /// <summary>逻辑→物理地址翻译唯一入口（spec [H1]：planner 不自建映射）；覆盖不到返回 null。</summary>
    private static ulong? Translate(A2lDocument doc, ulong logicalAddress) =>
        XcpAddressMap.TryTranslate(doc, logicalAddress, out var physical) ? physical : null;

    /// <summary>
    /// DAQ 配置解析（单表 + PID 区 + 条目上限三重守卫，T9 review F1/F2）：
    /// <list type="bullet">
    /// <item>恰一个 DAQ_LIST 且表号必须为 0（spec §1：MAX_DAQ=1，轮转只控 list 0，
    /// 表号 ≠ 0 显式拒绝，禁止静默配成 list 0）。</item>
    /// <item>FIRST_PID 起 MaxOdts 个 ODT 的 PID 区必须落在 DTO PID 区
    ///（≤ <see cref="XcpPid.DaqDtoLast"/>=0xFB）内，不得触 0xFF/0xFE 响应区——
    /// 接收线程按 DTO 首字节分流，PID 区越界 = 会产出无法分流的 DTO。缺 FIRST_PID
    /// 按 0 兜底（与 AcquisitionPlan 占位基址同口径）。</item>
    /// <item>MAX_ODT_ENTRY_SIZE_DAQ 缺声明 fail-loud（对账前置，宁可不采不错采）；
    /// 声明值只允许收紧 spec 硬上限 4B，取 min(声明值, 4)。</item>
    /// </list>
    /// </summary>
    private static (ushort FirstPid, int MaxEntryBytes) ResolveDaqConfig(A2lModule module)
    {
        var daq = module.IfDataXcp?.Daq;
        if (daq?.MaxOdtEntrySizeDaq is not { } declaredEntrySize)
        {
            throw new InvalidOperationException(
                $"IF_DATA XCP DAQ block missing MAX_ODT_ENTRY_SIZE_DAQ (module '{module.Name}'): " +
                "packing criteria cannot be established (spec §3: consume the declared value, never invent a default).");
        }

        var lists = daq.Lists;
        if (lists is not { Count: 1 })
        {
            throw new InvalidOperationException(
                $"AcquisitionPlanner supports exactly one DAQ list (spec §1: MAX_DAQ=1); module '{module.Name}' declares {lists?.Count ?? 0}.");
        }

        if (lists[0].Number != 0)
        {
            throw new InvalidOperationException(
                $"DAQ_LIST_NUMBER must be 0 (spec §1: rotation controls list 0 only); module '{module.Name}' declares {lists[0].Number}.");
        }

        var firstPid = lists[0].FirstPid ?? 0;
        if (firstPid + (uint)(MaxOdts - 1) > XcpPid.DaqDtoLast)
        {
            throw new InvalidOperationException(
                $"DAQ PID window [{firstPid}, {firstPid + MaxOdts - 1}] exceeds the DTO PID space (last = {XcpPid.DaqDtoLast}): " +
                "0xFF/0xFE are response PIDs and the receive loop demultiplexes DTOs by first byte (spec §3 Receive).");
        }

        return ((ushort)firstPid, Math.Min((int)declaredEntrySize, MaxEntryBytes));
    }
}