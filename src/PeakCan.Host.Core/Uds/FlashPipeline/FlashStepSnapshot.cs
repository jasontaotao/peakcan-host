namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// An immutable execution-router view of a single flashing-pipeline step. The App-layer
/// <c>FlashStep</c> (observable, CommunityToolkit-backed, UI-bound) holds the editable
/// state an operator sees in the DataGrid; this record is the frozen snapshot the
/// Core-layer <see cref="PipelineExecutor"/> consumes at flash time. Keeping the executor
/// on a pure-Core type preserves the dependency direction (Core must not reference App).
/// <para>
/// PipelineExecutor only ever READS this snapshot — it never mutates parameters — so
/// the record carries exactly the execution-relevant fields (kind + dispatch parameters
/// + the failure-safety flag). Fields that don't apply to a given Kind are ignored.
/// </para>
/// </summary>
public sealed record FlashStepSnapshot
{
    /// <summary>Immutable step kind — drives the executor's dispatch switch.</summary>
    public required FlashStepKind Kind { get; init; }

    /// <summary>Whether this step runs. PiplineExecutor assumes the caller has already
    /// filtered disabled steps OUT of the snapshot list, so this is informational for
    /// progress labelling rather than a runtime gate.</summary>
    public required bool IsEnabled { get; init; }

    // ---- SecurityAccess (Kind == SecurityAccess) ----

    /// <summary>Security access level (1–0x7F). Ignored for non-SecurityAccess kinds.</summary>
    public byte SecurityLevel { get; init; } = 0x01;

    /// <summary>Manual / Dll / Auto — selects which SecurityAccessAsync overload to call.</summary>
    public SecurityAccessMode SecurityMode { get; init; } = SecurityAccessMode.Manual;

    /// <summary>Hex string for Manual mode; PipelineExecutor hex-decodes into the SendKey payload.</summary>
    public string ManualKeyHex { get; init; } = string.Empty;

    /// <summary>Native DLL file path for Dll mode.</summary>
    public string DllPath { get; init; } = string.Empty;

    // ---- RoutineControl (Kind == Erase | Verify) ----

    /// <summary>2-byte routine ID. 0xFF00 for Erase default, operator-filled for Verify.</summary>
    public ushort RoutineId { get; init; }

    // ---- DownloadTransfer (Kind == DownloadTransfer) ----

    /// <summary>Target memory address for RequestDownload. Operator-filled.</summary>
    public uint MemoryAddress { get; init; }

    // ---- EcuReset (Kind == EcuReset) ----

    /// <summary>ECU reset sub-function (cast to byte, passed to EcuResetAsync).</summary>
    public EcuResetType ResetType { get; init; } = EcuResetType.HardReset;

    // ---- Cross-cutting safety net ----

    /// <summary>On failure, PipelineExecutor triggers EcuResetAsync(0x01) if this is true.</summary>
    public bool AutoResetOnFailure { get; init; } = true;
}
