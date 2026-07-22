using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

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

    // ---- SecurityAccess step parameters (Kind == SecurityAccess) ----

    /// <summary>
    /// SecurityAccess level (1–0x7F per ISO 14229-1 §11.4). Default 0x01 (programming unlock).
    /// </summary>
    [ObservableProperty]
    private byte _securityLevel = 0x01;

    /// <summary>
    /// Whether to derive the key from typed hex, an OEM DLL, or a DI-registered algorithm.
    /// Default <see cref="SecurityAccessMode.Manual"/> — the never-blocked fallback.
    /// </summary>
    [ObservableProperty]
    private SecurityAccessMode _securityMode = SecurityAccessMode.Manual;

    /// <summary>
    /// Operator-typed hex for <see cref="SecurityAccessMode.Manual"/>. Empty string at rest.
    /// Hex digits only (no 0x prefix); PipelineExecutor hex-decodes into the SendKey payload.
    /// </summary>
    [ObservableProperty]
    private string _manualKeyHex = string.Empty;

    /// <summary>
    /// OEM native DLL file path for <see cref="SecurityAccessMode.Dll"/>.
    /// Passed to <c>DllKeyDerivationAlgorithm</c>'s production ctor at flash time.
    /// </summary>
    [ObservableProperty]
    private string _dllPath = string.Empty;

    // ---- RoutineControl step parameters (Kind == Erase | Verify) ----

    /// <summary>
    /// 2-byte RoutineControl ID. Defaults 0xFF00 for Erase (de-facto industry EraseMemory
    /// routine, ISO 14229-1 §F.1), 0 for Verify (OEM-gated — operator must fill).
    /// </summary>
    [ObservableProperty]
    private ushort _routineId;

    // ---- DownloadTransfer step parameters (Kind == DownloadTransfer) ----

    /// <summary>Path to the firmware file. Empty until the operator picks a file.</summary>
    [ObservableProperty]
    private string _firmwarePath = string.Empty;

    /// <summary>
    /// Target memory address for RequestDownload (0x34). 0 until operator fills it —
    /// PipelineExecutor refuses Start on a zero address (avoids silently writing to 0).
    /// </summary>
    [ObservableProperty]
    private uint _memoryAddress;

    // ---- EcuReset step parameters (Kind == EcuReset) ----

    /// <summary>
    /// ECU reset sub-function byte. Default <see cref="EcuResetType.HardReset"/> (0x01) —
    /// the post-flash boot that starts the new image.
    /// </summary>
    [ObservableProperty]
    private EcuResetType _resetType = EcuResetType.HardReset;

    // ---- Cross-cutting safety-net flag (used by DownloadTransfer but shared lifetime) ----

    /// <summary>
    /// On an unhandled step failure, PipelineExecutor triggers <c>EcuResetAsync(0x01)</c>
    /// so the ECU is not left half-flashed. Default true (safest). Operator may opt out for
    /// OEMs that forbid auto-reset on failure.
    /// </summary>
    [ObservableProperty]
    private bool _autoResetOnFailure = true;

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

        // Per-kind parameter defaults that differ from the field initializers above.
        if (kind is FlashStepKind.Erase) _routineId = 0xFF00;
        // Verify stays RoutineId 0 (operator must fill, unlike Erase's de-facto 0xFF00).
    }
}
