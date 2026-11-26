namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Read-only explanation of one transaction. <see cref="DecodedValue"/> is null
/// whenever a degrade reason is set — the report keeps the raw payload and shows
/// the reason instead of guessing.
/// </summary>
public sealed record UdsInterpretation(
    string ServiceDisplay,
    string? DidName,
    string? RoutineName,
    string? DecodedValue,
    UdsDegradeReason? DegradeReason,
    string? DegradeDetail);
