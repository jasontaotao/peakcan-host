namespace PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// Single data point in the HIL pass-rate trend history.
/// </summary>
public sealed record TrendEntry(
    DateTime Timestamp,
    string SuiteName,
    int TotalCases,
    int PassedCases,
    int FailedCases,
    int ElapsedMs);
