namespace PeakCan.HIL.Core.HIL.Diff;

/// <summary>
/// Result of a diff comparison between two frame sequences.
/// </summary>
public sealed record DiffResult(
    int TotalGolden,
    int TotalActual,
    int Matched,
    int Added,
    int Removed,
    int Modified,
    IReadOnlyList<DiffEntry> Entries)
{
    public bool IsMatch => Added == 0 && Removed == 0 && Modified == 0;

    public double MatchRate => TotalGolden > 0 ? (double)Matched / TotalGolden : 0.0;
}
