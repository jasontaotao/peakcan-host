using PeakCan.HIL.Core.Uds.Database;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Immutable snapshot of the definition sources used for one analysis run.
/// Passed in by the App layer (Task 8) so the Core never reaches into
/// <c>DidDatabase</c> / <c>OdxImportViewModel</c> state mid-analysis — a running
/// report can then never be hot-updated by an ODX import that happens later.
/// </summary>
public sealed record UdsOfflineDiagnostics(
    IReadOnlyList<DidDefinition> Dids,
    IReadOnlyList<RoutineDefinition> Routines,
    bool OdxLoaded,
    UserJsonLoadStatus DidUserJsonStatus,
    UserJsonLoadStatus RoutineUserJsonStatus,
    string SnapshotId);
