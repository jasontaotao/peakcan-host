using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 2026-08-31 P4：ID 统计面板（spec §5.7）。底栏 Expander（<see cref="StatsExpanded"/>）
/// 展开时随批次刷新 Top-20 talker；点击行设为过滤。数据源复用
/// <see cref="GetMessageIdStats"/>。
/// </summary>
public sealed partial class TraceViewModel
{
    /// <summary>统计面板行集（Top-N talker）。</summary>
    public ObservableCollection<MessageIdStatRow> StatsRows { get; } = new();

    /// <summary>
    /// 重建 <see cref="StatsRows"/>：复用 <see cref="GetMessageIdStats(topN)"/>，
    /// 每行经 DBC 解析 <see cref="MessageIdStatRow.DbcName"/>（无则空）。
    /// 收起不刷；展开即刷；批次末（StatsExpanded 时）由 <c>AppendBatchCore</c> 调用。
    /// </summary>
    /// <summary>
    /// 统计面板最短刷新间隔。面板是给人看的 Top-20 榜单，5 Hz（每批）重建毫无意义：
    /// 每次都要对全部不同 ID 排序 + Clear + 最多 20 次 Add（21 条集合通知）。
    /// 500 ms 已是人眼可辨的实时感上限。
    /// </summary>
    private static readonly TimeSpan StatsRefreshInterval = TimeSpan.FromMilliseconds(500);

    private DateTime _lastStatsRefreshUtc = DateTime.MinValue;

    private void RefreshStats()
    {
        var doc = _dbcService?.Current;
        var stats = GetMessageIdStats(topN: 20);

        // ① ID 集合与顺序都没变 → 只原地更新数值，不碰集合（零集合通知）。
        //    这是稳态下的常见路径：Top-20 榜单在几百毫秒内通常完全不变。
        if (StatsRows.Count == stats.Count && SameIdsInOrder(stats))
        {
            for (var i = 0; i < stats.Count; i++)
            {
                var row = StatsRows[i];
                row.Count = stats[i].Count;
                row.Percent = stats[i].Percent;
            }
            return;
        }

        // ② 集合变了（新 ID 上榜 / 排序变化）→ 重建。这条路只在真正需要时走。
        StatsRows.Clear();
        foreach (var s in stats)
        {
            var dbcName = doc?.MessagesById.TryGetValue(s.RawId | 0x8000_0000u, out var m) == true
                ? m.Name
                : doc?.MessagesById.TryGetValue(s.RawId, out var m2) == true
                    ? m2.Name
                    : null;
            StatsRows.Add(new MessageIdStatRow
            {
                IdHex = s.IdHex,
                DbcName = dbcName,
                Count = s.Count,
                Percent = s.Percent,
            });
        }
    }

    /// <summary>统计行的 ID 序列是否与最新快照逐一对应（顺序敏感，因为榜单按计数排序）。</summary>
    private bool SameIdsInOrder(IReadOnlyList<MessageIdStat> stats)
    {
        for (var i = 0; i < stats.Count; i++)
        {
            if (!string.Equals(StatsRows[i].IdHex, stats[i].IdHex, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 批次末调用：限流到 <see cref="StatsRefreshInterval"/>。
    /// 面板收起时直接不刷（原行为），展开时也不超过 2 Hz。
    /// </summary>
    private void RefreshStatsThrottled()
    {
        var now = DateTime.UtcNow;
        if (now - _lastStatsRefreshUtc < StatsRefreshInterval) return;
        _lastStatsRefreshUtc = now;
        RefreshStats();
    }

    /// <summary>底栏 Expander 展开瞬间立即刷一次。</summary>
    partial void OnStatsExpandedChanged(bool value)
    {
        if (value) RefreshStats();
    }

    /// <summary>
    /// 统计行 → 设为过滤：**覆盖**写 <see cref="IdListText"/>（覆盖手填内容，刻意——
    /// 统计行的语义就是"只看这个 ID"）→ 走正常 spec 重建管线。闭环依据：
    /// <c>_messageCounts</c> key 是裸 <c>f.Id.Raw</c>，<see cref="MessageIdStatRow.IdHex"/>
    /// 已是 0x 前缀裸 ID，经 <see cref="TraceFilterParser"/> hex 解析后与 <c>entry.Id.Raw</c> 直接匹配。
    /// <para>
    /// 与 <see cref="ShowOnlyThisId"/> 同口径：先清掉会**反转/并集**的另外两个条件——
    /// 排除模式会把"只看这个 ID"整体取反；DBC 消息名会把它的全部 ID 并进 allow-list。
    /// 不先清，用户之前留下的开关会让这次点击得到相反的结果。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void SetFilterToId(MessageIdStatRow row)
    {
        if (row is null) return;
        DbcMessageName = "";
        ExcludeMatch = false;
        IdListText = row.IdHex; // 触发 OnIdListTextChanged → TryRebuildSpec
    }
}
