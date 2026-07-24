using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

// ---- Phase 2: Parameter groups per Kind (P7: { get; set; } for runtime editing) ----

/// <summary>PreCheck (预编程校验) parameters. Only meaningful when <see cref="FlashStep.Kind"/>
/// is <see cref="FlashStepKind.PreCheck"/>.</summary>
public sealed record PreCheckParams
{
    public ushort RoutineId { get; set; } = 0x0000;  // 操作员填写预检查 routine
}

/// <summary>SecurityAccess (0x27) parameters. Only meaningful when <see cref="FlashStep.Kind"/>
/// is <see cref="FlashStepKind.SecurityAccess"/>.</summary>
public sealed record SecurityAccessParams
{
    public byte Level { get; set; } = 0x01;
    public SecurityAccessMode Mode { get; set; } = SecurityAccessMode.Manual;
    public string ManualKeyHex { get; set; } = "";
    public string DllPath { get; set; } = "";
}

/// <summary>Erase/Verify (0x31 RoutineControl) parameters.</summary>
public sealed record RoutineControlParams
{
    public ushort RoutineId { get; set; } = 0xFF00;
    public uint StartAddress { get; set; }   // Phase 2 新增: 擦除起始地址
    public uint Size { get; set; }            // Phase 2 新增: 擦除大小
}

/// <summary>DownloadTransfer (0x34/0x36/0x37) parameters.</summary>
public sealed record DownloadParams
{
    public int SegmentIndex { get; set; }     // 引用 FirmwareFile.Segments[index]
    // MemoryAddress 不再需要 — 从 Segment.StartAddress 自动获取
}

/// <summary>Verify (0x31 RoutineControl for checksum) parameters.</summary>
public sealed record VerifyParams
{
    public ChecksumAlgorithm Algorithm { get; set; } = ChecksumAlgorithm.Crc32;
    public int SegmentIndex { get; set; }            // 引用 FirmwareFiles 扁平化 Segment 列表
    public uint ExpectedChecksum { get; set; }      // 从 Segment 自动计算（ToSnapshot 赋值）
    public uint StartAddress { get; set; }           // 从 Segment 自动计算
    public uint EndAddress { get; set; }             // 从 Segment 自动计算
}

/// <summary>EcuReset (0x11) parameters.</summary>
public sealed record EcuResetParams
{
    public EcuResetType ResetType { get; set; } = EcuResetType.HardReset;
}

/// <summary>Checksum algorithm for Verify step.</summary>
public enum ChecksumAlgorithm { Crc32 = 1, Crc16 = 2, OemDefined = 3 }

/// <summary>Phase 2: CommunicationControl (0x28) parameters.</summary>
public sealed record CommunicationControlParams
{
    public Core.Uds.CommunicationSubFunction SubFunction { get; set; } = Core.Uds.CommunicationSubFunction.DisableRxAndTx;
}

/// <summary>Phase 2: DTCControl (0x14) parameters.</summary>
public sealed record DtcControlParams
{
    public Core.Uds.DtcControlSubFunction SubFunction { get; set; } = Core.Uds.DtcControlSubFunction.ClearDTCInformation;
    public uint DtcGroup { get; set; } = 0x00FFFFFF;  // All DTCs
}


/// <summary>
/// One configurable row of the UDS flashing pipeline. The flashing view binds an
/// <c>ObservableCollection<FlashStep></c> (populated from a <see cref="FlashProfile"/>
/// default template) and the operator toggles <see cref="IsEnabled"/> / edits parameter
/// fields before Start. <see cref="PipelineExecutor"/> walks the enabled steps in order.
///
/// <para>
/// <see cref="Kind"/> is immutable after construction: the pipeline <b>shape</b> (which
/// services run, in what order) is fixed at template time and only the per-step
/// parameters and the enable flag are editable. This prevents an operator from silently
/// turning an <see cref="FlashStepKind.Erase"/> row into an
/// <see cref="FlashStepKind.EcuReset"/> (which would skip the destructive erase and flash
/// directly). A row's parameter properties that don't apply to its <see cref="Kind"/>
/// are simply unused — they keep a single observable row shape so the DataGrid columns
/// can stay uniform; PipelineExecutor only reads the parameters relevant to each Kind.
/// </para>
/// </summary>
public sealed partial class FlashStep : ObservableObject
{
    /// <summary>
    /// The pipeline row kind. Immutable — set only at construction. See class doc
    /// for why the shape is locked.
    /// </summary>
    public FlashStepKind Kind { get; }

    /// <summary>
    /// Whether this step runs when the operator presses Start. Defaults to true for
    /// every documented default-template step EXCEPT <see cref="FlashStepKind.PreCheck"/>
    /// (Phase 1 greyed placeholder) and <see cref="FlashStepKind.Verify"/>
    /// (OEM-gated optional step). Bound to the row checkbox.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    /// Phase 2: CAN addressing mode. Physical for most steps (target one ECU),
    /// Functional for broadcast steps like CommunicationControl (0x28).
    /// Uses Core.AddressingMode (Core must not reference App).
    /// </summary>
    [ObservableProperty]
    private Core.Uds.FlashPipeline.AddressingMode _addressingMode = Core.Uds.FlashPipeline.AddressingMode.Physical;

    // ---- Phase 2: Grouped parameters per Kind ----
    // Only the group matching Kind is non-null; others stay null (property panel hides them).

    /// <summary>PreCheck parameters. Non-null only when Kind == PreCheck.</summary>
    public PreCheckParams? PreCheck { get; private set; }

    /// <summary>SecurityAccess parameters. Non-null only when Kind == SecurityAccess.</summary>
    public SecurityAccessParams? SecurityAccess { get; private set; }

    /// <summary>Erase/Verify (RoutineControl) parameters. Non-null only when Kind == Erase or Verify.</summary>
    public RoutineControlParams? RoutineControl { get; private set; }

    /// <summary>DownloadTransfer parameters. Non-null only when Kind == DownloadTransfer.</summary>
    public DownloadParams? Download { get; private set; }

    /// <summary>Verify (checksum) parameters. Non-null only when Kind == Verify.</summary>
    public VerifyParams? Verify { get; private set; }

    /// <summary>EcuReset parameters. Non-null only when Kind == EcuReset.</summary>
    public EcuResetParams? EcuReset { get; private set; }

    /// <summary>CommunicationControl (0x28) parameters. Non-null only when Kind == CommunicationControl.</summary>
    public CommunicationControlParams? CommunicationControl { get; private set; }

    /// <summary>DTCControl (0x14) parameters. Non-null only when Kind == DtcControl.</summary>
    public DtcControlParams? DtcControl { get; private set; }

    // ---- Backward-compat flat fields (Phase 1.1) — kept for snapshot + existing tests ----
    // These mirror the grouped params above. Phase 2 UI binds to grouped params;
    // these remain for ToSnapshot() and backward compatibility.

    [ObservableProperty] private byte _securityLevel = 0x01;
    [ObservableProperty] private SecurityAccessMode _securityMode = SecurityAccessMode.Manual;
    [ObservableProperty] private string _manualKeyHex = string.Empty;
    [ObservableProperty] private string _dllPath = string.Empty;
    [ObservableProperty] private ushort _routineId;
    [ObservableProperty] private string _firmwarePath = string.Empty;
    [ObservableProperty] private uint _memoryAddress;
    [ObservableProperty] private EcuResetType _resetType = EcuResetType.HardReset;
    [ObservableProperty] private bool _autoResetOnFailure = true;

    /// <summary>
    /// Construct a step of the given kind with kind-appropriate defaults.
    /// Kind is locked; only parameters and IsEnabled are editable afterwards.
    /// <para>
    /// <see cref="JsonConstructorAttribute"/> directs System.Text.Json to use
    /// this ctor when deserializing a <see cref="FlashProfile"/>: <see cref="Kind"/>
    /// has no public setter, so the wire format's <c>Kind</c> value can only enter
    /// through the ctor. The remaining observable properties (with generated setters)
    /// are then bound from the Json post-construction. Parameter name <c>kind</c>
    /// matches the <c>Kind</c> property by Json's default case-insensitive matching.
    /// </para>
    /// </summary>
    /// <param name="kind">The immutable step kind.</param>
    [JsonConstructor]
    public FlashStep(FlashStepKind kind)
    {
        Kind = kind;

        // Per-kind default state. Two kinds ship disabled by default:
        //  - PreCheck: Phase 1 greyed placeholder; the enum value exists so the UI can
        //    render "Coming in Phase N", but the step does nothing yet.
        //  - Verify : OEM-gated optional; default off per the design total案 default template (☐ ⑥).
        _isEnabled = kind is not (FlashStepKind.PreCheck or FlashStepKind.Verify);

        // Phase 2: Initialize the parameter group matching this Kind.
        switch (kind)
        {
            case FlashStepKind.PreCheck:
                PreCheck = new PreCheckParams();
                break;

            case FlashStepKind.SecurityAccess:
                SecurityAccess = new SecurityAccessParams();
                // Sync flat fields from group (keeps snapshot working).
                _securityLevel = SecurityAccess.Level;
                _securityMode = SecurityAccess.Mode;
                _manualKeyHex = SecurityAccess.ManualKeyHex;
                _dllPath = SecurityAccess.DllPath;
                break;

            case FlashStepKind.Erase:
                RoutineControl = new RoutineControlParams { RoutineId = 0xFF00 };
                _routineId = RoutineControl.RoutineId;
                break;

            case FlashStepKind.Verify:
                RoutineControl = new RoutineControlParams { RoutineId = 0 };  // Verify: operator must fill (no de-facto 0xFF00)
                Verify = new VerifyParams();
                _routineId = 0;
                break;

            case FlashStepKind.DownloadTransfer:
                Download = new DownloadParams();
                break;

            case FlashStepKind.EcuReset:
                EcuReset = new EcuResetParams();
                _resetType = EcuReset.ResetType;
                break;

            case FlashStepKind.CommunicationControl:
                CommunicationControl = new CommunicationControlParams();
                break;

            case FlashStepKind.DtcControl:
                DtcControl = new DtcControlParams();
                break;
        }

        // Per-kind parameter defaults that differ from the field initializers above.
        if (kind is FlashStepKind.Erase) _routineId = 0xFF00;
        // Verify stays RoutineId 0 (operator must fill, unlike Erase's de-facto 0xFF00).
    }

    /// <summary>Phase 2: Set SecurityAccess level (helper for templates).</summary>
    public void SetSecurityAccessLevel(byte level)
    {
        if (SecurityAccess is null) return;
        SecurityAccess = SecurityAccess with { Level = level };
        SecurityLevel = level;  // sync flat field (uses generated property)
    }
}
