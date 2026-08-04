namespace PeakCan.HIL.Core.HIL.Diff;

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
                // DIFF-02 fix: use config.Tolerance for frame comparison
                if (FramesEqualWithTolerance(golden[i], actual[i], config.Tolerance))
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
        // DIFF-06 fix: Event-level compares by CAN ID only (no flags check in current implementation).
        // Simplified: index-based alignment. Future: add flags comparison and time-window matching.
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

    /// <summary>
    /// DIFF-02 fix: frame comparison with tolerance.
    /// When Tolerance is null or zero, behaves like exact match.
    /// When Tolerance is set, allows byte-level differences within absolute tolerance.
    /// </summary>
    private static bool FramesEqualWithTolerance(CanFrame a, CanFrame b, ToleranceSpec? tolerance)
    {
        if (a.Id.Raw != b.Id.Raw) return false;
        if (a.Data.Length != b.Data.Length) return false;
        // No tolerance configured — exact match
        if (tolerance is null || (tolerance.AbsoluteTolerance == 0 && tolerance.RelativeTolerance == 0))
            return a.Data.Span.SequenceEqual(b.Data.Span);

        // Compare byte-by-byte with absolute tolerance
        var absTol = tolerance.AbsoluteTolerance;
        var spanA = a.Data.Span;
        var spanB = b.Data.Span;
        for (int i = 0; i < spanA.Length; i++)
        {
            var diff = Math.Abs((double)spanA[i] - (double)spanB[i]);
            if (diff > absTol) return false;
        }
        return true;
    }

    /// <summary>Exact frame equality (no tolerance).</summary>
    private static bool FramesEqual(CanFrame a, CanFrame b) => FramesEqualWithTolerance(a, b, null);
}
