namespace PeakCan.Host.Core.HIL.Diff;

/// <summary>
/// Frame-level diff engine implementation.
/// Two constructors:
/// - DiffEngine(): frame-level diff (no DBC needed)
/// - DiffEngine(IDbcLookup): signal-level diff (DBC required for decode)
/// </summary>
internal sealed class DiffEngine : IDiffEngine
{
    private readonly Contracts.IDbcLookup? _dbcLookup;

    public DiffEngine() { }
    public DiffEngine(Contracts.IDbcLookup dbcLookup) => _dbcLookup = dbcLookup;

    public DiffResult Diff(
        IReadOnlyList<CanFrame> golden,
        IReadOnlyList<CanFrame> actual,
        DiffConfig config)
    {
        config.Validate();

        if (config.Granularity == DiffGranularity.Signal && _dbcLookup is null)
            throw new InvalidOperationException(
                "Signal-level diff requires IDbcLookup. Use DiffEngine(IDbcLookup) constructor.");

        return config.Granularity switch
        {
            DiffGranularity.Frame => FrameDiff(golden, actual, config),
            DiffGranularity.Event => EventDiff(golden, actual, config),
            _ => throw new NotImplementedException($"Granularity {config.Granularity} not yet implemented."),
        };
    }

    private static DiffResult FrameDiff(
        IReadOnlyList<CanFrame> golden,
        IReadOnlyList<CanFrame> actual,
        DiffConfig config)
    {
        var entries = new List<DiffEntry>();
        int matched = 0, modified = 0, added = 0, removed = 0;

        int maxLen = Math.Max(golden.Count, actual.Count);
        for (int i = 0; i < maxLen; i++)
        {
            bool hasGolden = i < golden.Count;
            bool hasActual = i < actual.Count;

            if (hasGolden && hasActual)
            {
                if (FramesEqual(golden[i], actual[i]))
                {
                    entries.Add(new DiffEntry(DiffEntryType.Matched, i, i, null, golden[i], actual[i]));
                    matched++;
                }
                else
                {
                    entries.Add(new DiffEntry(DiffEntryType.Modified, i, i, "Data differs", golden[i], actual[i]));
                    modified++;
                }
            }
            else if (hasGolden)
            {
                entries.Add(new DiffEntry(DiffEntryType.Removed, i, null, null, golden[i], null));
                removed++;
            }
            else
            {
                entries.Add(new DiffEntry(DiffEntryType.Added, null, i, null, null, actual[i]));
                added++;
            }
        }

        return new DiffResult(golden.Count, actual.Count, matched, added, removed, modified, entries);
    }

    private static DiffResult EventDiff(
        IReadOnlyList<CanFrame> golden,
        IReadOnlyList<CanFrame> actual,
        DiffConfig config)
    {
        // Event-level: match by frame presence within time window (simplified: index-based)
        // For Sprint 1, use same logic as frame-level but with ID+flags only (ignore data)
        var entries = new List<DiffEntry>();
        int matched = 0, modified = 0, added = 0, removed = 0;

        int maxLen = Math.Max(golden.Count, actual.Count);
        for (int i = 0; i < maxLen; i++)
        {
            bool hasGolden = i < golden.Count;
            bool hasActual = i < actual.Count;

            if (hasGolden && hasActual)
            {
                if (golden[i].Id.Raw == actual[i].Id.Raw)
                {
                    entries.Add(new DiffEntry(DiffEntryType.Matched, i, i, null, golden[i], actual[i]));
                    matched++;
                }
                else
                {
                    entries.Add(new DiffEntry(DiffEntryType.Modified, i, i, "ID differs", golden[i], actual[i]));
                    modified++;
                }
            }
            else if (hasGolden)
            {
                entries.Add(new DiffEntry(DiffEntryType.Removed, i, null, null, golden[i], null));
                removed++;
            }
            else
            {
                entries.Add(new DiffEntry(DiffEntryType.Added, null, i, null, null, actual[i]));
                added++;
            }
        }

        return new DiffResult(golden.Count, actual.Count, matched, added, removed, modified, entries);
    }

    private static bool FramesEqual(CanFrame a, CanFrame b)
    {
        if (a.Id.Raw != b.Id.Raw) return false;
        if (a.Data.Length != b.Data.Length) return false;
        return a.Data.Span.SequenceEqual(b.Data.Span);
    }
}
