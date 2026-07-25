using System.Windows;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// <see cref="IChatToolContext"/> implementation on
/// <see cref="TraceViewerViewModel"/>. Exposes the watch list, anchor
/// timestamps, current DBC, and seek/refresh operations to chat tools.
/// </summary>
/// <remarks>
/// All mutators marshal to the UI thread via
/// <see cref="Application.Current"/>.Dispatcher - tools run on the
/// thread-pool (Parallel.ForEachAsync in <c>ChatFlow</c>) but the state
/// they touch (<c>ObservableCollection</c>, OxyPlot annotations,
/// <c>ITraceViewerService.Seek</c>) is UI-affined.
/// </remarks>
public sealed partial class TraceViewerViewModel
{
    double IChatToolContext.AnchorTimestampSeconds => _anchorTimestampSeconds;
    double IChatToolContext.BlueAnchorTimestampSeconds => _blueAnchorTimestampSeconds;
    DbcDocument? IChatToolContext.CurrentDbc => _dbcService.Current;
    System.Collections.Generic.IReadOnlyList<WatchedSignalRow> IChatToolContext.WatchedSignals => WatchedSignals;

    void IChatToolContext.AddWatchedSignals(System.Collections.Generic.IEnumerable<WatchedSignalRow> rows)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var row in rows)
                WatchedSignals.Add(row);
        });
        // Anchor refresh is driven by ProposeToWatchListTool calling
        // RefreshAtAnchor/RefreshAtAnchorBlue after Add returns.
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
}
