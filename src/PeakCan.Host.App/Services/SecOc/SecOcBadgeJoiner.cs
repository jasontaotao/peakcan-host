using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel.SecOc;

namespace PeakCan.Host.App.Services.SecOc;

/// <summary>
/// Trace 徽章 join（spec §5-D6.7）：把 (handle, seq) 键的旁路 verdict 表翻译为
/// 每帧 <see cref="SecOcBadge"/>。seq 对齐原理：SecOcChannel._rxSequence 只对
/// 受保护帧递增且从 1 起；本类 per-handle 计数同规则同起点，链路（Record→同步
/// Invoke→router 保序→trace 按序处理）保证两侧顺序一致。
/// 线程安全：Join 在 UI 线程（trace 同步核心）；Configure/Reset 也在 UI 线程
/// （coordinator 生命周期），ConcurrentDictionary 用于跨线程安全兜底。
/// </summary>
public sealed class SecOcBadgeJoiner
{
    private readonly SecOcVerdictTable _table;
    private readonly ConcurrentDictionary<ushort, HashSet<uint>> _protectedByHandle = new();
    private readonly ConcurrentDictionary<ushort, long> _seqByHandle = new();

    public SecOcBadgeJoiner(SecOcVerdictTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>声明某通道的受保护 CAN ID 集合并归零 seq（连接时由 coordinator 调用）。</summary>
    public void Configure(ushort handle, IEnumerable<uint> protectedIds)
    {
        _protectedByHandle[handle] = new HashSet<uint>(protectedIds);
        _seqByHandle[handle] = 0;
    }

    /// <summary>单通道重置（与 verdict 表的 Clear(handle) 同步调用）。</summary>
    public void Reset(ushort handle)
    {
        _protectedByHandle.TryRemove(handle, out _);
        _seqByHandle.TryRemove(handle, out _);
    }

    /// <summary>全局重置（与 verdict 表的 Clear() 同步调用）。</summary>
    public void ResetAll()
    {
        _protectedByHandle.Clear();
        _seqByHandle.Clear();
    }

    /// <summary>per-frame 徽章求值。未配置通道 → 离线不验；未保护 ID → 未保护。</summary>
    public SecOcBadge Join(CanFrame frame)
    {
        var handle = frame.Channel.Handle;
        if (!_protectedByHandle.TryGetValue(handle, out var ids))
            return SecOcBadge.Offline;
        if (!ids.Contains(frame.Id.Raw))
            return SecOcBadge.Unprotected;

        var seq = _seqByHandle.AddOrUpdate(handle, 1, (_, s) => s + 1);
        return _table.TryGet(handle, seq, out var verdict)
            ? verdict.Accepted
                ? SecOcBadge.Accepted
                : SecOcBadge.Rejected(verdict.Reason?.ToString() ?? "unknown")
            : SecOcBadge.Offline; // seq 错位/表已清：禁止无标注，落灰
    }
}