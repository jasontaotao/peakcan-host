using System.Globalization;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow C: Signal table + filter (v3.15.0 MINOR + earlier patches).
    // Methods moved verbatim from TraceViewerViewModel.cs.
    //
    // Cross-flow callers (must be Flow[X]_<Verb> with internal visibility
    // after Tasks 3+5+6 land):
    //   - Flow A: FlowA_OnRegistrySourcesChanged calls RefreshFrameCounts (here)
    //   - Flow D: AddToWatch + FinalizePickerAdds call RefreshFrameCounts
    //   - Flow E: FlowE_ApplySnapshotAsync calls RebuildSignalsCore

    // v3.4.2 PATCH: filter changes trigger a synchronous rebuild via the
    // extracted core. Property change notifications fire on the UI thread,
    // and the core is fully synchronous — no Task continuation race.
    partial void OnCanIdFilterChanged(string value)
    {
        RebuildSignalsCore();
    }

    /// <summary>
    /// v3.15.0 MINOR: rebuild only re-runs the per-watch-list frame
    /// refresh; WatchedSignals is NOT cleared (user's watch list
    /// is the source of truth, survives rebuild). The legacy
    /// v3.14.3 `Signals` collection is left in place but no longer
    /// populated — it will be removed when the v3.14.3 tests are
    /// migrated to WatchedSignals. BuildChartSeries (v3.14.3 stub)
    /// stays as no-op since chart rows are still user opt-in via
    /// TogglePlot.
    /// </summary>
    private void RebuildSignalsCore()
    {
        var placeholders = WatchedSignals.Where(w => w.IsPlaceholder).ToList();
        foreach (var ph in placeholders)
            WatchedSignals.Remove(ph);
        if (_dbcService.Current is not null)
        {
            RefreshFrameCounts();
        }
        EnsurePlaceholderRow();
        ChartViewModel.SyncYAxes();
        ChartViewModel.SyncXAxis(0, _masterService?.TotalDuration ?? 0);
    }

    /// <summary>
    /// v3.14.3 PATCH + v3.15.0 MINOR: re-walk every loaded source's
    /// frames, count matches per (CAN ID, signal name) pair, and
    /// update each existing <see cref="WatchedSignalRow.FrameCount"/>
    /// + <see cref="WatchedSignalRow.LatestValue"/> in place. Iterates
    /// <see cref="WatchedSignals"/> (the user's explicit watch list),
    /// NOT the v3.14.3 DBC 全列. Does NOT clear WatchedSignals or
    /// <see cref="TraceChartViewModel.Series"/> — user watch entries
    /// (chart rows) survive.
    /// </summary>
    private void RefreshFrameCounts()
    {
        if (_dbcService.Current is null) return;
        var dbc = _dbcService.Current;
        var allowed = CanIdListParser.Parse(CanIdFilter).AllowList;
        var byId = BucketFramesByCanId(allowed);
        foreach (var row in WatchedSignals)
        {
            // Skip the placeholder row (no real canId to decode).
            if (row.IsPlaceholder) continue;

            var dot = row.SignalKey.IndexOf('.');
            if (dot <= 0) continue;
            var idHexStr = row.SignalKey.Substring(0, dot);
            if (!idHexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(idHexStr.AsSpan(2),
                               System.Globalization.NumberStyles.HexNumber,
                               null, out var canId)) continue;
            var lookupId = canId & 0x7FFFFFFFu;
            var matching = byId.TryGetValue(lookupId, out var list) ? list : null;

            // v3.15.0 MINOR: source-pinned watches filter the frame
            // count by that specific SourceId. Cross-source watches
            // (SourceId == null) see all-source totals.
            int count;
            ReplayFrame? lastFrame = null;
            if (row.SourceId is null)
            {
                count = matching?.Count ?? 0;
                lastFrame = matching is { Count: > 0 } ? matching[^1] : null;
            }
            else
            {
                var perSourceFrames = matching?.Where(f =>
                {
                    // Look up which source this frame belongs to by
                    // walking _registry.Sources — ReplayFrame doesn't
                    // carry SourceId. For now, count by per-source
                    // registry lookup.
                    var src = _registry.Sources.FirstOrDefault(s =>
                        _registry.GetFrames(s.SourceId).Contains(f));
                    return src?.SourceId == row.SourceId;
                }).ToList();
                count = perSourceFrames?.Count ?? 0;
                lastFrame = perSourceFrames is { Count: > 0 } ? perSourceFrames[^1] : null;
            }

            row.FrameCount = count;
            if (lastFrame is not null)
            {
                // v3.50.2 PATCH: prefer the cached Signal reference set by
                // OnWatchedSignalsCollectionChangedForSignalCache so the
                // decode uses the same DBC Signal the chart series uses
                // (sister of v3.50 GreenLineAnchorFlow.RecomputeAllLatestAtAnchor).
                // Falls back to a DBC lookup only when the cache missed
                // (e.g. test fixtures that bypass CollectionChanged).
                var sig = row.Signal
                    ?? dbc.Messages
                        .Where(m => (m.Id & 0x7FFFFFFFu) == lookupId)
                        .SelectMany(m => m.Signals)
                        .FirstOrDefault(s => s.Name == row.SignalName);
                if (sig is not null)
                {
                    var decoded = SignalDecoder.Decode(lastFrame.Data, sig);
                    row.LatestValue = decoded;
                    // v3.50.2 PATCH: do NOT mirror BlueLatestValue here.
                    // Before the user drags the blue anchor,
                    // BlueLatestValue stays NaN and the Δ column renders
                    // as "—" (no comparison target chosen yet). Once
                    // the user right-click-drags the blue anchor,
                    // RecomputeAllLatestAtBlueAnchor overwrites this
                    // with the actual anchor-time decode.
                    // (Earlier v3.50.2 build mirrored here; user
                    // feedback: Δ = 0 hides whether the user has
                    // actually compared or not.)
                }
            }
        }
    }

    /// <summary>
    /// v3.11.0 MINOR T4 (H8): bucket all loaded frames by CAN ID across
    /// every registered source, applying per-source overrides of the
    /// global allow-list. Returns a dict from CAN ID → ordered list of
    /// matching <see cref="ReplayFrame"/>s (insertion order = source
    /// iteration order, which matches the registry's order). Replaces
    /// the first half of the original 145-LoC <c>RebuildSignalsCore</c>
    /// body.
    /// </summary>
    private Dictionary<uint, List<ReplayFrame>> BucketFramesByCanId(IReadOnlySet<uint>? globalAllowed)
    {
        // v3.2.0 MINOR: bucket frames from all loaded sources by CAN ID.
        var byId = new Dictionary<uint, List<ReplayFrame>>();
        foreach (var source in _registry.Sources)
        {
            // v3.4.3 PATCH: per-source filter overrides the global one. Empty
            // per-source → fall through to globalAllowed (inherit). Non-empty
            // → use the per-source parse result exclusively.
            var perSourceAllowed = CanIdListParser.Parse(source.CanIdFilter).AllowList;
            var effective = perSourceAllowed ?? globalAllowed;
            foreach (var f in _registry.GetFrames(source.SourceId))
            {
                if (effective is not null && !effective.Contains(f.Id)) continue;
                if (!byId.TryGetValue(f.Id, out var list))
                {
                    list = new List<ReplayFrame>();
                    byId[f.Id] = list;
                }
                list.Add(f);
            }
        }
        return byId;
    }

    /// <summary>
    /// v3.14.3 PATCH: DBC-driven signal row builder. Walks every
    /// message + every signal in <paramref name="dbc"/> and emits
    /// exactly one row per signal — even if no frames match. The
    /// <see cref="TraceSignalRow.FrameCount"/> and
    /// <see cref="TraceSignalRow.LatestValue"/> columns are populated
    /// from the bucket (default 0 / NaN when no frames exist).
    /// <para>
    /// The signal catalog is independent of whether data has been
    /// loaded: the user sees every DBC signal the moment a DBC is
    /// loaded, and <see cref="RefreshFrameCounts"/> updates the
    /// <c>N</c> + <c>Latest</c> columns in place when an .asc arrives.
    /// </para>
    /// <para>
    /// Rows are sorted by (CanIdHex, SignalName) ordinal order,
    /// preserving the pre-v3.14.3 sort key.
    /// </para>
    /// </summary>
    private List<TraceSignalRow> BuildSignalRowsFromDbcOnly(
        Dictionary<uint, List<ReplayFrame>> byId,
        DbcDocument dbc)
    {
        var rows = new List<TraceSignalRow>();
        foreach (var msg in dbc.Messages)
        {
            // v3.14.1 PATCH: strip the DBC IDE-bit (0x80000000) before
            // looking up in byId. The DBC stores extended-frame IDs with
            // the IDE bit set in bit 31, but BucketFramesByCanId keys by
            // raw ASC frame ids which are 29-bit (no IDE bit). Mask the
            // DBC side to match. msg.Id itself is preserved (callers
            // can still see the original via msg.Id).
            var maskedId = msg.Id & 0x7FFFFFFFu;
            byId.TryGetValue(maskedId, out var matching);
            var frameCount = matching?.Count ?? 0;
            var idHex = FormatCanIdHex(maskedId);
            foreach (var sig in msg.Signals)
            {
                double latest = double.NaN;
                if (matching is { Count: > 0 })
                {
                    latest = SignalDecoder.Decode(matching[^1].Data, sig);
                }
                rows.Add(new TraceSignalRow(
                    canIdHex: idHex,
                    messageName: msg.Name,
                    signalName: sig.Name,
                    unit: sig.Unit,
                    isPlotted: false,
                    frameCount: frameCount,
                    latestValue: latest));
            }
        }

        rows.Sort(static (a, b) =>
        {
            var byId2 = string.CompareOrdinal(a.CanIdHex, b.CanIdHex);
            return byId2 != 0 ? byId2 : string.CompareOrdinal(a.SignalName, b.SignalName);
        });
        return rows;
    }
}