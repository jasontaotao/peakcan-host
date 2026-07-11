using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow D: Watch list + chart plotting (v3.15.0 MINOR + earlier patches).
    // Methods moved verbatim from TraceViewerViewModel.cs.
    //
    // Cross-flow references (all stay as plain calls via partial-class visibility):
    //   - AddToWatch + FinalizePickerAdds → RefreshFrameCounts (Flow C)
    //   - PlotSignalFromTableRow → BuildOneChartSeriesForSource (Flow D helper, stays in main file)
    //   - PlotSignalFromTableRow → ChartViewModel.AddSeries/SyncYAxes/SyncXAxis (TraceChartViewModel members)

    /// <summary>
    /// v3.14.3 PATCH: opt-in/opt-out handler invoked from the DataGrid
    /// checkbox Click handler in <c>TraceViewerView.xaml.cs</c>.
    /// Decides whether to add or remove chart series based on the
    /// new <see cref="TraceSignalRow.IsPlotted"/> value (the binding
    /// updates it before this method fires, thanks to
    /// <c>UpdateSourceTrigger=PropertyChanged</c>).
    /// </summary>
    [RelayCommand]
    public void TogglePlot(WatchedSignalRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (row.IsPlotted)
            PlotSignalFromTableRow(row);
        else
            UnplotSignalFromTableRow(row);
    }

    /// <summary>
    /// v3.14.3 PATCH: explicit opt-in. Tests and programmatic callers
    /// use this directly (no binding lag concerns). Production XAML
    /// uses <see cref="TogglePlot(WatchedSignalRow)"/> which inspects
    /// the row's <see cref="WatchedSignalRow.IsPlotted"/> after the
    /// binding has updated it.
    /// </summary>
    public void SetPlotOptIn(WatchedSignalRow row, bool optIn)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (optIn)
            PlotSignalFromTableRow(row);
        else
            UnplotSignalFromTableRow(row);
    }

    /// <summary>
    /// v3.14.3 PATCH back-compat: legacy overload accepting the old
    /// <see cref="TraceSignalRow"/> record. Wraps the call by
    /// forwarding via the row's INPC fields (SignalKey / SignalName /
    /// CanIdHex). New code should call the
    /// <see cref="SetPlotOptIn(WatchedSignalRow, bool)"/> overload
    /// directly. The wrapping builds a transient
    /// <see cref="WatchedSignalRow"/> from the legacy row's fields.
    /// </summary>
    public void SetPlotOptIn(TraceSignalRow row, bool optIn)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var transient = new WatchedSignalRow(
            canIdHex: row.CanIdHex,
            messageName: "",
            signalName: row.SignalName,
            unit: row.Unit);
        SetPlotOptIn(transient, optIn);
    }

    /// <summary>
    /// v3.15.0 MINOR: add a signal to the user's watch list. Invoked
    /// from the <c>+ Add to watch…</c> toolbar button (which opens a
    /// <c>DbcTreePickerWindow</c> for the user to pick a message +
    /// signal). Creates a new <see cref="WatchedSignalRow"/>,
    /// appends to <see cref="WatchedSignals"/>, and immediately plots
    /// the chart series for the watched source(s). Idempotent on
    /// duplicate (canId, signalName, sourceId) — silently no-ops if
    /// the row already exists.
    /// <para>
    /// Not decorated with <c>[RelayCommand]</c> because the toolkit's
    /// generator does not support 3-arg signatures. Callers (XAML
    /// code-behind, programmatic) invoke this method directly.
    /// </para>
    /// </summary>
    public void AddToWatch(uint canId, string signalName, string sourceId)
    {
        if (_dbcService.Current is null) return;
        var dbc = _dbcService.Current;

        // Lookup the message + signal in the DBC.
        var maskedId = canId & 0x7FFFFFFFu;
        var msg = dbc.Messages.FirstOrDefault(m => (m.Id & 0x7FFFFFFFu) == maskedId);
        if (msg is null) return;
        var sig = msg.Signals.FirstOrDefault(s => s.Name == signalName);
        if (sig is null) return;

        // Treat empty string as "all sources" (cross-source watch).
        string? pinnedSource = string.IsNullOrEmpty(sourceId) ? null : sourceId;

        // Idempotent: dedupe on (canId, signalName, sourceId).
        var canIdHex = FormatCanIdHex(maskedId);
        var existing = WatchedSignals.FirstOrDefault(w =>
            !w.IsPlaceholder
            && w.CanIdHex == canIdHex
            && w.SignalName == signalName
            && w.SourceId == pinnedSource);
        if (existing is not null) return;

        var row = new WatchedSignalRow(
            canIdHex: canIdHex,
            messageName: msg.Name,
            signalName: signalName,
            unit: sig.Unit,
            sourceId: pinnedSource);
        // v3.16.2 PATCH: back to the original single-call semantics —
        // Add + RefreshFrameCounts + Plot + remove placeholder all in
        // one pass. Safe when called once (tests, programmatic) — the
        // ItemContainerGenerator confusion only happens with rapid
        // bursts of multiple AddToWatch calls (the picker flow), which
        // uses the new AddToWatchForPicker + FinalizePickerAdds pair.
        WatchedSignals.Add(row);

        // v3.15.0 MINOR: refresh FrameCount + LatestValue for the new
        // row from the current bucket so the watch list immediately
        // shows how many frames are available.
        RefreshFrameCounts();

        // Auto-plot: the user just added this — show them the data
        // immediately. PlotSignalFromTableRow accepts a WatchedSignalRow.
        PlotSignalFromTableRow(row);

        // Drop any placeholder row when the first real watch entry is added.
        var placeholders = WatchedSignals.Where(w => w.IsPlaceholder).ToList();
        foreach (var ph in placeholders)
            WatchedSignals.Remove(ph);
    }

    /// <summary>
    /// v3.16.2 PATCH: picker-friendly AddToWatch that returns the
    /// created row. The caller collects all rows in a list, then
    /// invokes <see cref="FinalizePickerAdds"/> once to drop the
    /// placeholder + refresh frame counts + plot — keeping the
    /// WatchedSignals collection edit pattern as "N adds then 1
    /// remove" (WPF ItemContainerGenerator-friendly) rather than
    /// the previous "add + remove + add + add + remove" interleave.
    /// </summary>
    public WatchedSignalRow AddToWatchForPicker(uint canId, string signalName, string sourceId)
    {
        if (_dbcService.Current is null) return null!;
        var dbc = _dbcService.Current;
        var maskedId = canId & 0x7FFFFFFFu;
        var msg = dbc.Messages.FirstOrDefault(m => (m.Id & 0x7FFFFFFFu) == maskedId);
        if (msg is null) return null!;
        var sig = msg.Signals.FirstOrDefault(s => s.Name == signalName);
        if (sig is null) return null!;

        string? pinnedSource = string.IsNullOrEmpty(sourceId) ? null : sourceId;
        var canIdHex = FormatCanIdHex(maskedId);
        var existing = WatchedSignals.FirstOrDefault(w =>
            !w.IsPlaceholder
            && w.CanIdHex == canIdHex
            && w.SignalName == signalName
            && w.SourceId == pinnedSource);
        if (existing is not null) return existing;

        var row = new WatchedSignalRow(
            canIdHex: canIdHex,
            messageName: msg.Name,
            signalName: signalName,
            unit: sig.Unit,
            sourceId: pinnedSource);
        WatchedSignals.Add(row);
        return row;
    }

    /// <summary>
    /// v3.16.2 PATCH: finalize a batch of picker additions. Drops
    /// any placeholder, refreshes frame counts, and plots each
    /// added row. Designed to be called once after the picker
    /// returns, so the WatchedSignals collection has a single
    /// "add N rows" event followed by a single "remove placeholder"
    /// event (rather than the interleave that caused
    /// ItemContainerGenerator confusion in v3.16.1).
    /// <para>
    /// v3.16.6 PATCH BUGFIX (2-agent root-cause): the v3.16.2 design
    /// still fired N Add + 1 Remove events back-to-back, which races
    /// with the WPF ItemContainerGenerator's Recycling-mode bookkeeping
    /// when DataGrid EnableRowVirtualization=True. With 6+ signals
    /// selected in the picker, the Generator's cumulative count drifted
    /// by 1 from Items.Count ("累计计数 5 与实际计数 6 不相同") and
    /// threw InvalidOperationException on the next Refresh pass.
    /// <b>Fix:</b> collapse the picker finalize to a single Reset
    /// event (Clear) followed by all real rows in a deterministic
    /// order. Clear() forces the Generator to re-sync its cumulative
    /// count to 0 before the N Adds land — the cumulative count then
    /// matches Items.Count exactly. RefreshFrameCounts runs after
    /// the rebuild so FrameCount is correct on the first binding
    /// pass (no stale 0 values).
    /// </para>
    /// </summary>
    public void FinalizePickerAdds(IReadOnlyList<WatchedSignalRow> addedRows)
    {
        if (addedRows is null || addedRows.Count == 0)
        {
            // Even on no-op, ensure placeholder state is correct.
            EnsurePlaceholderRow();
            return;
        }

        // v3.16.6 PATCH: snapshot real rows (skip placeholder), Clear
        // the collection (1 Reset event → Generator re-syncs cumulative
        // count to 0), then re-Add the kept rows + the new picker rows
        // in a deterministic order. This avoids the N-Add + 1-Remove
        // interleave that races with WPF's Recycling generator
        // bookkeeping when DataGrid is virtualized.
        // v3.16.6 PATCH (B1 of this PATCH): dedupe addedRows against
        // `kept` by WatchId. AddToWatchForPicker returns the EXISTING
        // row (not a new one) when the user re-picks an already-watched
        // signal, and FinalizePickerAdds's contract is "addedRows are
        // rows to plot" — adding them all back to the collection would
        // double-list existing watches after the Clear. Match by WatchId
        // (the dedupe key) so the post-Clear state is identical to the
        // pre-Clear state + newly-added rows.
        var kept = WatchedSignals.Where(w => !w.IsPlaceholder).ToList();
        var keptIds = new HashSet<string>(kept.Select(r => r.WatchId), StringComparer.Ordinal);
        WatchedSignals.Clear();
        foreach (var row in kept) WatchedSignals.Add(row);
        foreach (var row in addedRows)
        {
            if (row is null) continue;
            if (keptIds.Contains(row.WatchId)) continue;  // already in kept — no re-add
            WatchedSignals.Add(row);
        }

        // Refresh frame counts for the watch list (now sees all rows).
        RefreshFrameCounts();

        // Plot each newly added row.
        foreach (var row in addedRows)
        {
            if (row is null) continue;
            PlotSignalFromTableRow(row);
        }
    }

    /// <summary>
    /// v3.15.0 MINOR: remove a watch entry. Unplots any chart series
    /// that came from this row, then removes from
    /// <see cref="WatchedSignals"/>.
    /// </summary>
    [RelayCommand]
    public void RemoveFromWatch(WatchedSignalRow row)
    {
        if (row is null) return;
        if (row.IsPlaceholder) return;
        UnplotSignalFromTableRow(row);
        WatchedSignals.Remove(row);
        EnsurePlaceholderRow();
    }

    /// <summary>
    /// v3.15.0 MINOR: ensure the watch list shows a contextual
    /// placeholder row when it's empty. Called from
    /// <see cref="RebuildSignalsCore"/> + <see cref="OnRegistrySourcesChanged"/>
    /// + <see cref="OnDbcLoaded"/> + <see cref="RemoveFromWatch"/>.
    /// </summary>
    private void EnsurePlaceholderRow()
    {
        // Don't add a duplicate placeholder.
        if (WatchedSignals.Any(w => w.IsPlaceholder)) return;
        var dbc = _dbcService.Current;
        var asc = _registry.Sources.Count;
        string msg;
        if (dbc is null && asc == 0)
            msg = "(no DBC and no .asc loaded — open DBC tab + File ▸ Add trace…)";
        else if (dbc is null)
            msg = "(no DBC loaded — open DBC from DBC tab to enable watch list)";
        else if (asc == 0)
            msg = "(no .asc loaded — File ▸ Add trace… to populate)";
        else
            msg = "(no signals in watch list — click + Add to watch…)";
        WatchedSignals.Add(new WatchedSignalRow(
            canIdHex: "—",
            messageName: msg,
            signalName: "",
            unit: "",
            isPlaceholder: true));
    }

    /// <summary>
    /// v3.15.0 MINOR: invoked by <see cref="TogglePlot"/> (legacy
    /// v3.14.3 path) and by <see cref="AddToWatch"/> (new v3.15.0
    /// path). Creates one <see cref="TraceChartSeries"/> per source
    /// that has matching frames. Graceful no-op if no source has
    /// frames (user can still toggle; nothing to chart).
    /// </summary>
    private void PlotSignalFromTableRow(WatchedSignalRow row)
    {
        if (_dbcService.Current is null) return;
        var dbc = _dbcService.Current;
        var dot = row.SignalKey.IndexOf('.');
        if (dot <= 0) return;
        var idHexStr = row.SignalKey.Substring(0, dot);
        if (!idHexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
        if (!uint.TryParse(idHexStr.AsSpan(2),
                           System.Globalization.NumberStyles.HexNumber,
                           null, out var canId)) return;
        var lookupId = canId & 0x7FFFFFFFu;
        var sig = dbc.Messages
            .Where(m => (m.Id & 0x7FFFFFFFu) == lookupId)
            .SelectMany(m => m.Signals)
            .FirstOrDefault(s => s.Name == row.SignalName);
        if (sig is null) return;

        var created = 0;
        TraceChartSeries? firstBuilt = null;
        foreach (var source in _registry.Sources)
        {
            // v3.15.0 MINOR: source-pinned watches only plot against
            // their pinned source; cross-source watches (SourceId null)
            // plot all sources.
            if (row.SourceId is not null && source.SourceId != row.SourceId) continue;
            var built = BuildOneChartSeriesForSource(source, sig, lookupId, row.CanIdHex, row.SignalName);
            if (built is null) continue;  // no frames in this source
            ChartViewModel.AddSeries(built);
            created++;
            firstBuilt ??= built;
        }
        if (created > 0)
        {
            ChartViewModel.SyncYAxes();
            // v3.16.5 PATCH BUGFIX (4-agent root-cause): use the new
            // series' own XValues range, not the master service's
            // [CurrentTimestamp, TotalDuration]. The previous
            // CurrentTimestamp-based call overwrote EVERY series' X
            // axis (the loop in SyncXAxis iterates Series), and
            // CurrentTimestamp during playback = the live cursor
            // (e.g. 350s into a 650s trace), which narrowed the X
            // range to [350, 650] and pushed xs[0]..xs[N-1] frames
            // with x < 350 outside the viewport — OxyPlot rendered
            // the line off-canvas and the user saw "no chart".
            // Mirrors the working PlotSignal path at line 1725.
            var xMin = firstBuilt!.XValues[0];
            var xMax = firstBuilt.XValues[^1];
            // Defensive: if a degenerate series (single point), fall
            // back to master service's full range so OxyPlot has a
            // non-zero axis width.
            if (xMax <= xMin)
            {
                xMin = 0;
                xMax = _masterService?.TotalDuration > 0 ? _masterService.TotalDuration : 1.0;
            }
            ChartViewModel.SyncXAxis(xMin, xMax);
        }
    }

    /// <summary>
    /// v3.14.3 PATCH + v3.15.0 MINOR: remove all chart series whose
    /// <see cref="TraceChartSeries.SignalKey"/> matches
    /// <paramref name="row"/>.SignalKey. Inverse of
    /// <see cref="PlotSignalFromTableRow"/>.
    /// </summary>
    private void UnplotSignalFromTableRow(WatchedSignalRow row)
    {
        var key = row.SignalKey;
        // Snapshot because RemoveSeries mutates the collection.
        var matches = ChartViewModel.Series
            .Where(s => s.SignalKey == key
                        || s.SignalKey.EndsWith("." + key, StringComparison.Ordinal))
            .ToList();
        foreach (var s in matches)
            ChartViewModel.RemoveSeries(s);
    }
}