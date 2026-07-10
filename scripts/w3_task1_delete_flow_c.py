"""Delete Flow C methods from TraceViewerViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")

content = MAIN.read_text(encoding="utf-8")

# Delete RebuildSignalsCore + its xmldoc (line 1510-1536)
old_block_1 = """    /// <summary>
    /// v3.4.2 PATCH: synchronous core of <see cref="RebuildSignalsAsync"/>.
    /// Extracted so property-setter change handlers (synchronous context)
    /// can invoke the rebuild without the async-Task continuation race.
    /// The body has no real awaits — it's fully synchronous in practice.
    /// </summary>
    private void RebuildSignalsCore()
    {
        // v3.15.0 MINOR: rebuild only re-runs the per-watch-list frame
        // refresh; WatchedSignals is NOT cleared (user's watch list
        // is the source of truth, survives rebuild). The legacy
        // v3.14.3 `Signals` collection is left in place but no longer
        // populated — it will be removed when the v3.14.3 tests are
        // migrated to WatchedSignals. BuildChartSeries (v3.14.3 stub)
        // stays as no-op since chart rows are still user opt-in via
        // TogglePlot.
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

"""
assert old_block_1 in content, "Block 1 (RebuildSignalsCore) not found"
content = content.replace(old_block_1, "")
print("Block 1 (RebuildSignalsCore) deleted")

# Delete BucketFramesByCanId + its xmldoc
old_block_2 = """    /// <summary>
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

"""
assert old_block_2 in content, "Block 2 (BucketFramesByCanId) not found"
content = content.replace(old_block_2, "")
print("Block 2 (BucketFramesByCanId) deleted")

# Delete BuildSignalRowsFromDbcOnly + its xmldoc
old_block_3 = """    /// <summary>
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

"""
assert old_block_3 in content, "Block 3 (BuildSignalRowsFromDbcOnly) not found"
content = content.replace(old_block_3, "")
print("Block 3 (BuildSignalRowsFromDbcOnly) deleted")

# Delete RefreshFrameCounts + its xmldoc
old_block_4 = """    /// <summary>
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
                var sig = dbc.Messages
                    .Where(m => (m.Id & 0x7FFFFFFFu) == lookupId)
                    .SelectMany(m => m.Signals)
                    .FirstOrDefault(s => s.Name == row.SignalName);
                if (sig is not null)
                    row.LatestValue = SignalDecoder.Decode(lastFrame.Data, sig);
            }
        }
    }

"""
assert old_block_4 in content, "Block 4 (RefreshFrameCounts) not found"
content = content.replace(old_block_4, "")
print("Block 4 (RefreshFrameCounts) deleted")

# Add flow marker
marker_old = """    /// <summary>
    /// v3.14.3 PATCH: stub. Chart series are no longer auto-built at"""
marker_new = """    // === Flow C methods moved to TraceViewerViewModel/SignalFlow.cs (W3 Task 1) ===

    /// <summary>
    /// v3.14.3 PATCH: stub. Chart series are no longer auto-built at"""
content = content.replace(marker_old, marker_new)
print("Flow C marker added")

MAIN.write_text(content, encoding="utf-8")
print(f"New file size: {MAIN.stat().st_size} bytes")