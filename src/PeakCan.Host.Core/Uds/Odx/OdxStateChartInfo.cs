namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>A single state transition within an ODX STATE-CHART.</summary>
public sealed record StateChartTransition(
    string TransitionId,
    string SourceState,
    string TargetState);

/// <summary>
/// Extracted STATE-CHART info from an ODX document.
/// <see cref="StateNames"/> and <see cref="Transitions"/> reflect the chart's
/// STATES / STATE-TRANSITIONS definitions (from STATE-CHART element, not
/// the per-DIAG-SERVICE transition refs).
/// </summary>
public sealed record OdxStateChartInfo(
    string ChartName,
    string StartState,
    IReadOnlyList<string> StateNames,
    IReadOnlyList<StateChartTransition> Transitions);
