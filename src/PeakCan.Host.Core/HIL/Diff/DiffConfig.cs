namespace PeakCan.HIL.Core.HIL.Diff;

/// <summary>
/// Three-layer orthogonal diff configuration.
/// </summary>
public sealed record DiffConfig(
    DiffGranularity Granularity = DiffGranularity.Frame,
    AlignStrategy Alignment = AlignStrategy.Timestamp,
    ToleranceSpec Tolerance = default,
    int NeighborWindowMs = 100)
{
    /// <summary>
    /// Validates config invariants. Called by Diff() entry point to prevent with-expression bypass.
    /// </summary>
    public void Validate()
    {
        if (Alignment == AlignStrategy.NearestNeighbor && NeighborWindowMs <= 0)
            throw new ArgumentException(
                "NeighborWindowMs must be > 0 for NearestNeighbor alignment",
                nameof(NeighborWindowMs));
        if (Tolerance is not null && (Tolerance.AbsoluteTolerance < 0 || Tolerance.RelativeTolerance < 0))
            throw new ArgumentException("Tolerance cannot be negative", nameof(Tolerance));
    }
}
