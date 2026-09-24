using System;
using System.Collections.Generic;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>
/// 轮询降级原因（S2-T9 planner 自产归因）。包侧 <c>A2lEditor.Core.Layout.MissingCause</c>
/// 是"合同缺失/解码不可用"归因，语义不同，不复用。
/// </summary>
public enum PlannedPollingCause
{
    /// <summary>对象总长 &gt; 4B（MAX_ODT_ENTRY_SIZE_DAQ），单条 DAQ 条目装不下。</summary>
    ObjectTooLarge,

    /// <summary>非 1/2/4 整类字节量（位域/非字节对齐量；XCP_BITOFFSET_SUPPORT STD_OFF，进不了 DAQ）。</summary>
    NotByteAlignedClass,

    /// <summary>15 ODT × 7B 数据场装满后仍装不下（超 105 B/拍），余量自动降级。</summary>
    DaqCapacityExceeded,
}

/// <summary>
/// DAQ 打包后的单条目（planner 自产编号，spec §3 Scheduling [H2]：不是解析期占位三元组）。
/// <para>协议字段口径：Pid 是 DTO 首字节的 DAQ PID（ODT 序号 = Pid − FIRST_PID）；
/// EntryIndex 是 ODT 内条目序；OffsetInOdt 是本条目在 ODT 数据场内的字节偏移。</para>
/// </summary>
public sealed record PlannedDaqEntry(
    uint Pid,
    ushort OdtIndex,
    ushort EntryIndex,
    string ObjectName,
    int SegmentIndex,
    int ByteLength,
    int OffsetInOdt);

/// <summary>打包后的单个 ODT：同类同箱（同一 ODT 内条目字节长恒等于 <see cref="SizeClassBytes"/>）。</summary>
public sealed record PlannedOdt(
    ushort OdtIndex,
    uint Pid,
    int SizeClassBytes,
    IReadOnlyList<PlannedDaqEntry> Entries);

/// <summary>轮询降级条目：逻辑地址恒有；物理地址经包侧 XcpAddressMap.TryTranslate 求得，覆盖不到为 null。</summary>
public sealed record PlannedPollingEntry(
    string ObjectName,
    int SegmentIndex,
    int ByteLength,
    int SourceOffset,
    ulong LogicalAddress,
    ulong? PhysicalAddress,
    PlannedPollingCause Cause);

/// <summary>
/// AcquisitionPlanner 的输出方案（S2-T9，spec §3 Scheduling）：DAQ 打包（ODT 分配）+
/// 轮询降级集合 + 打包反查索引。构造期一次建好、之后只读，可并发读。
/// <para>
/// [H2]：本方案的 (Pid, OdtIndex, EntryIndex) 由 planner 按 7B 装箱自建——
/// 绝不是 AcquisitionPlan 的解析期占位三元组（ODT 恒 0、FIRST_PID 起文档序、
/// 每对象一 entry），接收线程（T14）只准用本方案反查。
/// </para>
/// </summary>
public sealed class PlannedAcquisitionMap
{
    private readonly Dictionary<(uint Pid, ushort OdtIndex, ushort EntryIndex), PlannedDaqEntry> _byTriple;

    public PlannedAcquisitionMap(
        byte daqNumber,
        ushort odtCount,
        IReadOnlyList<PlannedOdt> odts,
        IReadOnlyList<PlannedPollingEntry> pollingEntries)
    {
        DaqNumber = daqNumber;
        OdtCount = odtCount;
        Odts = odts;
        PollingEntries = pollingEntries;

        // 反查索引构造期一次建好（§3 Receive：索引解析期一次建、只读、可并发）。
        _byTriple = new Dictionary<(uint, ushort, ushort), PlannedDaqEntry>();
        foreach (var odt in odts)
        foreach (var entry in odt.Entries)
            _byTriple[(entry.Pid, entry.OdtIndex, entry.EntryIndex)] = entry;
    }

    /// <summary>DAQ 表号（spec §1：单 DAQ 表，轮转只控 list 0）。</summary>
    public byte DaqNumber { get; }

    /// <summary>本方案实际占用的 ODT 数（≤ 15）。</summary>
    public ushort OdtCount { get; }

    /// <summary>打包后的 ODT 列表（按 ODT 序号升序）。</summary>
    public IReadOnlyList<PlannedOdt> Odts { get; }

    /// <summary>降级轮询集合（>4B / 非字节对齐 / 超容量；spec §1：超 105 B/拍自动降级）。</summary>
    public IReadOnlyList<PlannedPollingEntry> PollingEntries { get; }

    /// <summary>打包反查：(PID, ODT, Entry) → 条目。查不到返回 false。</summary>
    public bool TryResolve(uint pid, ushort odtIndex, ushort entryIndex, out PlannedDaqEntry entry) =>
        _byTriple.TryGetValue((pid, odtIndex, entryIndex), out entry!);
}