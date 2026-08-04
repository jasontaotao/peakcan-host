namespace PeakCan.HIL.Core.Uds.FlashPipeline;

/// <summary>Phase 2: CAN addressing mode. Physical = target one ECU, Functional = broadcast.</summary>
public enum AddressingMode
{
    Physical,
    Functional,
}

// Phase 2: Parameter group records mirrored from App-layer (Core must not reference App).
// These carry the Kind-specific execution parameters the executor consumes.

public sealed record PreCheckSnapshot(
    ushort RoutineId);

public sealed record SecurityAccessSnapshot(
    byte Level,
    SecurityAccessMode Mode,
    string ManualKeyHex,
    string DllPath,
    int? SeedLength);

public sealed record RoutineControlSnapshot(
    ushort RoutineId,
    uint StartAddress,
    uint Size);

public sealed record DownloadSnapshot(
    int SegmentIndex);

public sealed record VerifySnapshot(
    byte Algorithm,
    uint ExpectedChecksum,
    uint StartAddress,
    uint EndAddress,
    int SegmentIndex);  // M1: carried so the executor can distinguish "not configured" (index out of range) from "configured with checksum 0".

public sealed record EcuResetSnapshot(
    EcuResetType ResetType);

public sealed record CommunicationControlSnapshot(
    CommunicationSubFunction SubFunction);

public sealed record DtcControlSnapshot(
    byte SubFunction,
    uint DtcGroup);

public sealed record DependencyCheckSnapshot(
    ushort RoutineId);

public sealed record FlashDriverDownloadSnapshot();

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

    /// <summary>Phase 2: CAN addressing mode (Physical=target one ECU, Functional=broadcast).</summary>
    public AddressingMode AddressingMode { get; init; } = AddressingMode.Physical;

    // ---- Phase 2: Grouped parameters per Kind (only the matching group is non-null) ----

    public PreCheckSnapshot? PreCheck { get; init; }
    public SecurityAccessSnapshot? SecurityAccess { get; init; }
    public RoutineControlSnapshot? RoutineControl { get; init; }
    public DownloadSnapshot? Download { get; init; }
    public VerifySnapshot? Verify { get; init; }
    public EcuResetSnapshot? EcuReset { get; init; }
    public CommunicationControlSnapshot? CommunicationControl { get; init; }
    public DtcControlSnapshot? DtcControl { get; init; }
    public DependencyCheckSnapshot? DependencyCheck { get; init; }
    public FlashDriverDownloadSnapshot? FlashDriverDownload { get; init; }

    // ---- Backward-compat flat fields (Phase 1.1) — kept for existing tests + executor ----
    // Phase 2 executor reads from grouped params; these remain for backward compat.

    /// <summary>Security access level (1–0x7F). Ignored for non-SecurityAccess kinds.</summary>
    public byte SecurityLevel { get; init; } = 0x01;

    /// <summary>Manual / Dll / Auto — selects which SecurityAccessAsync overload to call.</summary>
    public SecurityAccessMode SecurityMode { get; init; } = SecurityAccessMode.Manual;

    /// <summary>Hex string for Manual mode; PipelineExecutor hex-decodes into the SendKey payload.</summary>
    public string ManualKeyHex { get; init; } = string.Empty;

    /// <summary>Native DLL file path for Dll mode.</summary>
    public string DllPath { get; init; } = string.Empty;

    /// <summary>Seed byte length (null = auto). Drives DLL seed padding/truncation.</summary>
    public int? SeedLength { get; init; } = null;

    /// <summary>2-byte routine ID. 0xFF00 for Erase default, operator-filled for Verify.</summary>
    public ushort RoutineId { get; init; }

    /// <summary>Target memory address for RequestDownload. Operator-filled.</summary>
    public uint MemoryAddress { get; init; }

    /// <summary>ECU reset sub-function (cast to byte, passed to EcuResetAsync).</summary>
    public EcuResetType ResetType { get; init; } = EcuResetType.HardReset;

    /// <summary>On failure, PipelineExecutor triggers EcuResetAsync(0x01) if this is true.</summary>
    public bool AutoResetOnFailure { get; init; } = true;
}
