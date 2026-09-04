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
    private void RefreshStats()
    {
        var doc = _dbcService?.Current;
        var stats = GetMessageIdStats(topN: 20);
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
    /// </summary>
    [RelayCommand]
    private void SetFilterToId(MessageIdStatRow row)
    {
        if (row is null) return;
        IdListText = row.IdHex; // 触发 OnIdListTextChanged → TryRebuildSpec
    }
}
