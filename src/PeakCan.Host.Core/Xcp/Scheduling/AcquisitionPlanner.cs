using System;
using System.Collections.Generic;
using System.Linq;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>
/// S2-T9 采集规划器（spec §3 Scheduling / §1 从机硬约束）：
/// <list type="bullet">
/// <item>7B 装箱：按尺寸类同类同箱——4B 条 1 条/ODT、2B 条 3 条/ODT、1B 条 7 条/ODT；
/// 单 DAQ 表 15 ODT、ODT 数据场 7B（DTO 8B − PID 1B）是硬上限，
/// MAX_ODT_ENTRIES=100 只是声明天花板，绝不参与容量计算。</item>
/// <item>溢出降级：15×7=105 B/拍是容量上限（实际受尺寸类 ODT 占用约束更紧），
/// ODT 预算耗尽后余量自动降级轮询。</item>
/// <item>位域/非字节对齐量与 &gt;4B 量直接归轮询（DAQ 装不下；
/// 真机 BIT_MASK 全 0、包契约未建位域字段——spec §5.6，非整类字节量是包侧能
/// 表达的同一形态）。</item>
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

    /// <summary>单条目字节上限（MAX_ODT_ENTRY_SIZE_DAQ，spec §1 实测基线 4）。</summary>
    public const int MaxEntryBytes = 4;

    // 装箱尺寸类与填充顺序（固定，保证确定性）：4B 先占箱、再 2B、最后 1B。
    private static readonly int[] SizeClasses = [4, 2, 1];

    /// <summary>装箱候选：一个对象的一个段（≤4B 且落进尺寸类才有资格进 DAQ）。</summary>
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
        var firstPid = ResolveFirstPid(module);

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

            Classify(doc, contract, polling, candidates);
        }

        // 装箱：逐尺寸类填 ODT（同类同箱），ODT 预算 15 耗尽后余量降级轮询
        //（= 超 105 B/拍的量自动降级，spec §1/§3）。
        var odts = new List<PlannedOdt>();
        ushort odtIndex = 0;
        foreach (var size in SizeClasses)
        {
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
                    entries.Add(new PlannedDaqEntry(
                        (uint)(firstPid + odtIndex), odtIndex, entryIndex,
                        item.ObjectName, item.SegmentIndex, item.ByteLength, offset));
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
    /// 对象分类：&gt;4B → 轮询（ObjectTooLarge）；总长不是 1/2/4 整类字节 → 轮询
    ///（NotByteAlignedClass，位域/非字节对齐同口径）；合格对象逐段进装箱候选
    ///（≤4B 对象单段，防御性校验段宽也必须落进尺寸类）。
    /// </summary>
    private static void Classify(
        A2lDocument doc,
        ValueContract contract,
        List<PlannedPollingEntry> polling,
        Dictionary<int, List<Candidate>> candidates)
    {
        if (contract.TotalByteLength > MaxEntryBytes)
        {
            PollEverything(doc, contract, PlannedPollingCause.ObjectTooLarge, polling);
            return;
        }

        if (!SizeClasses.Contains(contract.TotalByteLength))
        {
            PollEverything(doc, contract, PlannedPollingCause.NotByteAlignedClass, polling);
            return;
        }

        for (var i = 0; i < contract.Segments.Count; i++)
        {
            var segment = contract.Segments[i];
            if (!SizeClasses.Contains(segment.ByteLength))
            {
                // 病态切分（≤4B 对象拆出非整类段）不猜：整对象降级轮询。
                PollEverything(doc, contract, PlannedPollingCause.NotByteAlignedClass, polling);
                return;
            }

            if (!candidates.TryGetValue(segment.ByteLength, out var list))
                candidates[segment.ByteLength] = list = new List<Candidate>();
            list.Add(new Candidate(contract.ObjectName, i, segment.ByteLength, segment.SourceOffset, segment.Address));
        }
    }

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
    /// DAQ PID 基址：模块级 IF_DATA DAQ 块第一个 DAQ_LIST 的 FIRST_PID
    ///（真机 0x00）。缺块/缺值按 0 兜底（与 AcquisitionPlan 占位基址同口径）；
    /// 多个 DAQ_LIST 违反单表假设，直接拒绝。
    /// </summary>
    private static ushort ResolveFirstPid(A2lModule module)
    {
        var lists = module.IfDataXcp?.Daq?.Lists;
        if (lists is null)
            return 0;
        if (lists.Count > 1)
        {
            throw new InvalidOperationException(
                $"AcquisitionPlanner supports a single DAQ list (spec §1: MAX_DAQ=1); module '{module.Name}' declares {lists.Count}.");
        }

        var firstPid = lists.Count > 0 ? lists[0].FirstPid : null;
        if (firstPid > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"DAQ_LIST FIRST_PID {firstPid} exceeds the XCP 8-bit DAQ PID space (module '{module.Name}').");
        }

        return (ushort)(firstPid ?? 0);
    }
}