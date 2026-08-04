namespace PeakCan.HIL.Core.HIL.Diff;

/// <summary>
/// Tolerance specification for signal-level diff comparison.
/// </summary>
public sealed record ToleranceSpec(
    double AbsoluteTolerance = 0.0,
    double RelativeTolerance = 0.0)
{
    public bool IsWithin(double expected, double actual)
    {
        var diff = Math.Abs(expected - actual);
        return diff <= AbsoluteTolerance || diff <= Math.Abs(expected) * RelativeTolerance;
    }

    public static ToleranceSpec Exact => new(0.0, 0.0);
}
