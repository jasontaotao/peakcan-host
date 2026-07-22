namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// Lifecycle status of a flashing pipeline run, surfaced through
/// <see cref="FlashProgress.Status"/> to the UI. <see cref="PipelineExecutor"/>
/// reports exactly one transition into each of these over a run's lifetime.
/// </summary>
public enum FlashStatus
{
    /// <summary>The pipeline has not started or has been reset.</summary>
    Idle,

    /// <summary>A step is currently executing (request in flight or awaiting response).</summary>
    Running,

    /// <summary>The pipeline completed every enabled step successfully.</summary>
    Success,

    /// <summary>A step raised an exception; the run is aborting (post any auto-reset attempt).</summary>
    Failed,

    /// <summary>The operator cancelled via <c>CancellationToken</c>; the run stopped cleanly.</summary>
    Cancelled,
}

/// <summary>
/// One progress report emitted by <see cref="PipelineExecutor"/> via
/// <c>IProgress<FlashProgress></c> — the bridge from the Core executor back to the
/// App-layer <c>FlashPanelViewModel</c>'s UI-bound status/progress properties. Immutable
/// snapshot: each <see cref="IProgress{T}.Report"/> call posts a fresh value rather than
/// mutating shared state, so the UI thread always sees a consistent picture per report.
/// <para>
/// Sub-progress (<see cref="CurrentStepDoneBytes"/> / <see cref="CurrentStepTotalBytes"/>)
/// is populated only during a <see cref="FlashStepKind.DownloadTransfer"/> step where it
/// reflects the TransferData byte count; other steps leave them null so the UI hides the
/// per-byte bar.
/// </para>
/// </summary>
public sealed record FlashProgress
{
    /// <summary>1-based index of the step currently executing within the enabled sequence.</summary>
    public required int CurrentStepIndex { get; init; }

    /// <summary>Total number of enabled steps in the run (excludes toggled-off rows).</summary>
    public required int TotalSteps { get; init; }

    /// <summary>Kind of the step currently executing (for the UI's "Step 3/7: SecurityAccess" label).</summary>
    public required FlashStepKind CurrentStepKind { get; init; }

    /// <summary>Lifecycle status snapshot.</summary>
    public required FlashStatus Status { get; init; }

    /// <summary>
    /// Bytes transferred so far within the current DownloadTransfer step. Null for non-transfer steps.
    /// </summary>
    public ulong? CurrentStepDoneBytes { get; init; }

    /// <summary>
    /// Total bytes in this DownloadTransfer step (the image size). Null for non-transfer steps.
    /// </summary>
    public ulong? CurrentStepTotalBytes { get; init; }

    /// <summary>Human-readable message for the status line (last error text on Failed, etc.).</summary>
    public string? Message { get; init; }
}
