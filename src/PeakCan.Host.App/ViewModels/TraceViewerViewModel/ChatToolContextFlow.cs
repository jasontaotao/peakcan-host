using System.Collections.ObjectModel;
using System.Windows;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Trace;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// <see cref="IChatToolContext"/> implementation on
/// <see cref="TraceViewerViewModel"/>. Exposes the watch list, anchor
/// timestamps, current DBC, and seek/refresh operations to chat tools.
/// </summary>
/// <remarks>
/// All mutators marshal to the UI thread via
/// <see cref="Application.Current"/>.Dispatcher - tools run on the
/// thread-pool (sequential for-loop in <c>ChatFlow</c> since v12 C2)
/// but the state they touch (<c>ObservableCollection</c>, OxyPlot
/// annotations, <c>ITraceViewerService.Seek</c>) is UI-affined.
/// </remarks>
public sealed partial class TraceViewerViewModel
{
    // v3.x (会话状态剥离 Task 3): SignalGroups 已转发到 _session.SignalGroups
    // （声明在主文件 TraceViewerViewModel.cs），此处不再重复声明。

    double IChatToolContext.AnchorTimestampSeconds => _anchorTimestampSeconds;
    double IChatToolContext.BlueAnchorTimestampSeconds => _blueAnchorTimestampSeconds;
    DbcDocument? IChatToolContext.CurrentDbc => _dbcService.Current;
    IReadOnlyList<WatchedSignalRow> IChatToolContext.WatchedSignals => WatchedSignals;
    IReadOnlyList<WatchedSignalGroup> IChatToolContext.SignalGroups => SignalGroups;

    void IChatToolContext.AddWatchedSignals(IEnumerable<WatchedSignalRow> rows)
    {
        // v12 fix: Add + Plot + RefreshAnchors + CollectionView.Refresh must
        // be in the SAME Dispatcher.Invoke. When the Watch List tab is not
        // visible, the DataGrid's ItemContainerGenerator may miss Add events
        // during bulk Add + RefreshAtAnchor INPC bursts. Calling Refresh()
        // forces a Reset so the generator resyncs from the actual collection
        // state when the tab becomes visible again.
        Application.Current.Dispatcher.Invoke(() =>
        {
            var rowList = rows.ToList();
            foreach (var row in rowList)
                WatchedSignals.Add(row);
            // v12 fix: plot each new row on the chart (same as the manual
            // AddToWatch -> PlotSignalFromTableRow path). Without this,
            // AI-added signals appear in the watch list table but not on
            // the chart.
            foreach (var row in rowList)
                PlotSignalFromTableRow(row);
            // v12 fix: refresh FrameCount + LatestValue for new rows (same
            // as FinalizePickerAdds -> RefreshFrameCounts path). Without
            // this, AI-added signals show "--" in the Latest column.
            RefreshFrameCounts();
            if (!double.IsNaN(_anchorTimestampSeconds))
                RefreshAtAnchor(_anchorTimestampSeconds);
            if (!double.IsNaN(_blueAnchorTimestampSeconds))
                RefreshAtAnchorBlue(_blueAnchorTimestampSeconds);
            System.Windows.Data.CollectionViewSource
                .GetDefaultView(WatchedSignals).Refresh();
        });
    }

    bool IChatToolContext.RemoveWatchedSignal(string signalKey)
    {
        bool removed = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            for (int i = WatchedSignals.Count - 1; i >= 0; i--)
            {
                if (WatchedSignals[i].SignalKey == signalKey)
                {
                    WatchedSignals.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
            if (removed)
            {
                // Re-decode anchor values for remaining rows.
                if (!double.IsNaN(_anchorTimestampSeconds))
                    RefreshAtAnchor(_anchorTimestampSeconds);
                if (!double.IsNaN(_blueAnchorTimestampSeconds))
                    RefreshAtAnchorBlue(_blueAnchorTimestampSeconds);
                // Force CollectionView resync (same reason as AddWatchedSignals).
                System.Windows.Data.CollectionViewSource
                    .GetDefaultView(WatchedSignals).Refresh();
            }
        });
        return removed;
    }

    void IChatToolContext.RefreshAtAnchor(double timestampSeconds)
        => Application.Current.Dispatcher.Invoke(() => RefreshAtAnchor(timestampSeconds));

    void IChatToolContext.RefreshAtAnchorBlue(double timestampSeconds)
        => Application.Current.Dispatcher.Invoke(() => RefreshAtAnchorBlue(timestampSeconds));

    bool IChatToolContext.Seek(double timestampSeconds)
    {
        if (_masterService is null) return false;
        Application.Current.Dispatcher.Invoke(() => _masterService.Seek(timestampSeconds));
        return true;
    }

    TraceInfo IChatToolContext.GetTraceInfo()
    {
        var sources = _registry.Sources;
        var sourceInfos = new List<TraceSourceInfo>(sources.Count);
        foreach (var s in sources)
        {
            var frames = _registry.GetFrames(s.SourceId);
            sourceInfos.Add(new TraceSourceInfo(
                s.SourceId,
                s.DisplayName,
                s.Path,
                frames.Count,
                string.IsNullOrEmpty(s.CanIdFilter) ? null : s.CanIdFilter));
        }
        return new TraceInfo(
            TotalDuration: _masterService?.TotalDuration ?? 0.0,
            SourceCount: sources.Count,
            DbcLoaded: _dbcService.Current is not null,
            DbcPath: string.IsNullOrEmpty(_dbcService.Current?.SourcePath)
                ? null
                : _dbcService.Current!.SourcePath,
            CurrentTimestamp: _masterService?.CurrentTimestamp ?? 0.0,
            WallClockOrigin: sources.FirstOrDefault()?.WallClockOrigin,
            Sources: sourceInfos);
    }

    DbcInfo IChatToolContext.GetDbcInfo()
    {
        var dbc = _dbcService.Current;
        if (dbc is null)
            return new DbcInfo(null, 0, 0, Array.Empty<string>(), null);
        return new DbcInfo(
            Version: dbc.Version,
            MessageCount: dbc.Messages.Count,
            SignalCount: dbc.Messages.Sum(m => m.Signals.Count),
            Nodes: dbc.Nodes.Select(n => n.Name).ToList(),
            SourcePath: string.IsNullOrEmpty(dbc.SourcePath) ? null : dbc.SourcePath);
    }

    string IChatToolContext.CreateGroup(string name, IReadOnlyList<string>? signalKeys)
    {
        var group = new WatchedSignalGroup(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            Notes: null,
            SignalKeys: signalKeys is null || signalKeys.Count == 0
                ? Array.Empty<string>()
                : signalKeys.ToList());
        Application.Current.Dispatcher.Invoke(() => SignalGroups.Add(group));
        return group.Id;
    }

    int IChatToolContext.AddToGroup(string groupId, IReadOnlyList<string> signalKeys)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < SignalGroups.Count; i++)
            {
                if (SignalGroups[i].Id == groupId)
                {
                    var existing = SignalGroups[i].SignalKeys;
                    var toAdd = signalKeys.Except(existing).ToList();
                    if (toAdd.Count > 0)
                    {
                        SignalGroups[i] = SignalGroups[i] with
                        {
                            SignalKeys = existing.Concat(toAdd).ToList()
                        };
                    }
                    return toAdd.Count;
                }
            }
            return 0;
        });
    }

    int IChatToolContext.RemoveFromGroup(string groupId, IReadOnlyList<string> signalKeys)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < SignalGroups.Count; i++)
            {
                if (SignalGroups[i].Id == groupId)
                {
                    var existing = SignalGroups[i].SignalKeys;
                    var remaining = existing.Except(signalKeys).ToList();
                    int removed = existing.Count - remaining.Count;
                    if (removed > 0)
                    {
                        SignalGroups[i] = SignalGroups[i] with { SignalKeys = remaining };
                    }
                    return removed;
                }
            }
            return 0;
        });
    }

    void IChatToolContext.SetGroupNotes(string groupId, string notes)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < SignalGroups.Count; i++)
            {
                if (SignalGroups[i].Id == groupId)
                {
                    SignalGroups[i] = SignalGroups[i] with { Notes = notes };
                    return;
                }
            }
        });
    }

    void IChatToolContext.SetSignalAlias(string signalKey, string? alias)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var row in WatchedSignals)
            {
                if (row.SignalKey == signalKey)
                {
                    row.Alias = alias;
                    return;
                }
            }
        });
    }

    IReadOnlyList<ReplayFrame> IChatToolContext.GetFrames(string sourceId)
    {
        // J1939 重组虚拟帧并入：get_signal_overview / search_signal_trace / anomaly_scan /
        // analyze_timing_sequence 均经此接口取帧解码。多帧报文（BRM/BCP）在原始帧里只有
        // TP.CM/TP.DT，不并入则多帧信号按 DBC ID 过滤零命中 → AI Chat 报 "no frames"
        //（与 RefreshFrameCounts / 锚线同款 L3 并入；虚拟帧无源概念，多源时并入各源，
        // 与图表路径 Task 13 Finding 2 的同款已知限制）。
        var raw = _registry.GetFrames(sourceId);
        if (_j1939VirtualFrames.Count == 0) return raw;
        return raw.Concat(_j1939VirtualFrames).ToList();
    }
}
