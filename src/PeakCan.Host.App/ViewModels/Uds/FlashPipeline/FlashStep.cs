using System.ComponentModel;
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

/// <summary>
/// Erase/Verify (0x31 RoutineControl) parameters.
/// Implements <see cref="INotifyPropertyChanged"/> so the Erase step can auto-fill
/// StartAddress/Size when the operator picks a segment.
/// </summary>
public sealed class RoutineControlParams : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private ushort _routineId = 0xFF00;
    public ushort RoutineId
    {
        get => _routineId;
        set { _routineId = value; Raise(); }
    }

    private uint _startAddress;
    public uint StartAddress
    {
        get => _startAddress;
        set { _startAddress = value; Raise(); }
    }

    private uint _size;
    public uint Size
    {
        get => _size;
        set { _size = value; Raise(); }
    }

    /// <summary>
    /// Issue 1: Erase 步骤引用的 Segment 索引 (引用 FirmwareFiles 扁平化 Segment 列表).
    /// 选中后自动填充 StartAddress / Size, 避免 operator 手工填错.
    /// </summary>
    private int _segmentIndex = -1;  // -1 = 未选择 (手工填写模式)
    public int SegmentIndex
    {
        get => _segmentIndex;
        set
        {
            if (_segmentIndex == value) return;
            _segmentIndex = value;
            Raise();
        }
    }

    /// <summary>Issue 1: 当 operator 选择 Segment 后, 调用此方法自动填充 StartAddress/Size.</summary>
    public void ApplySegmentAddress(uint startAddress, uint size)
    {
        StartAddress = startAddress;
        Size = size;
    }
}

/// <summary>DownloadTransfer (0x34/0x36/0x37) parameters.</summary>
public sealed record DownloadParams
{
    public int SegmentIndex { get; set; }     // 引用 FirmwareFile.Segments[index]
    // MemoryAddress 不再需要 — 从 Segment.StartAddress 自动获取
}

/// <summary>
/// Verify (0x31 RoutineControl for checksum) parameters.
/// Implements <see cref="INotifyPropertyChanged"/> so the property panel can two-way bind
/// the CRC preset selector and have the dependent parameter fields refresh live.
/// </summary>
public sealed class VerifyParams : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ChecksumAlgorithm Algorithm { get; set; } = ChecksumAlgorithm.Crc32;

    private int _segmentIndex;
    public int SegmentIndex
    {
        get => _segmentIndex;
        set
        {
            if (_segmentIndex == value) return;
            _segmentIndex = value;
            Raise();
            // Issue: segment 索引变更时自动填充地址+CRC, 否则 UI 显示 0.
            if (SegmentResolver is not null && SegmentResolver(value) is { } seg)
            {
                StartAddress = seg.StartAddress;
                EndAddress = seg.EndAddress;
                // 用当前 CRC 参数重新算 ExpectedChecksum (预设或自定义均可).
                ExpectedChecksum = Crc32.Compute(seg.Data, _crcParameters);
            }
            else if (value < 0)
            {
                // 清空 — 通常是绑定初始化或步骤切换.
                StartAddress = 0;
                EndAddress = 0;
                ExpectedChecksum = 0;
            }
        }
    }

    /// <summary>
    /// Issue: 静态 Segment 解析器委托, 由 VM 在启动时设 (SegmentAtIndex).
    /// 当 SegmentIndex 变更时自动填充 StartAddress / EndAddress / ExpectedChecksum.
    /// </summary>
    public static Func<int, Core.Uds.FlashPipeline.Segment?>? SegmentResolver { get; set; }

    private uint _expectedChecksum;
    public uint ExpectedChecksum
    {
        get => _expectedChecksum;
        set { _expectedChecksum = value; Raise(); }
    }

    private uint _startAddress;
    public uint StartAddress
    {
        get => _startAddress;
        set { _startAddress = value; Raise(); }
    }

    private uint _endAddress;
    public uint EndAddress
    {
        get => _endAddress;
        set { _endAddress = value; Raise(); }
    }

    // The UI ComboBox has Presets.Count + 1 entries: indices 0..Presets.Count-1 are the
    // named presets, index Presets.Count is "Custom". Internally we store 0..Presets.Count-1
    // for presets and CustomSentinel (-1) for Custom.
    private const int CustomSentinel = -1;

    /// <summary>Issue 3: CRC 算法参数 (多项式 / 初值 / 终值异或 / 反转).</summary>
    private Core.Uds.FlashPipeline.CrcParameters _crcParameters = Core.Uds.FlashPipeline.CrcParameters.Crc32;
    public Core.Uds.FlashPipeline.CrcParameters CrcParameters
    {
        get => _crcParameters;
        set
        {
            _crcParameters = value;
            Raise();
            // If the edited parameters diverge from the currently-selected preset, switch to Custom.
            if (_selectedCrcPresetIndex >= 0
                && value != Core.Uds.FlashPipeline.CrcParameters.Presets[_selectedCrcPresetIndex])
            {
                _selectedCrcPresetIndex = CustomSentinel;
                Raise(nameof(SelectedCrcPresetIndex));
            }
            Raise(nameof(IsCrcCustom));
            // CRC 参数变更 → 重算 ExpectedChecksum (如果有有效 Segment).
            if (SegmentResolver is not null && SegmentResolver(_segmentIndex) is { } seg)
                ExpectedChecksum = Crc32.Compute(seg.Data, value);
        }
    }

    /// <summary>
    /// Issue 3: 选中的 CRC preset 索引 (0..3 对应 4 个预设, 4 = Custom).
    /// UI ComboBox 直接绑定此属性 (SelectedIndex); 选中 Custom 时内部映射到 -1.
    /// </summary>
    private int _selectedCrcPresetIndex;  // 默认 0 = CRC-32
    public int SelectedCrcPresetIndex
    {
        get => _selectedCrcPresetIndex == CustomSentinel
            ? Core.Uds.FlashPipeline.CrcParameters.Presets.Count  // Custom → last dropdown item
            : _selectedCrcPresetIndex;
        set
        {
            int mapped = value >= Core.Uds.FlashPipeline.CrcParameters.Presets.Count
                ? CustomSentinel
                : value;
            if (_selectedCrcPresetIndex == mapped) return;
            _selectedCrcPresetIndex = mapped;
            Raise();
            Raise(nameof(IsCrcCustom));
            if (mapped >= 0)
                CrcParameters = Core.Uds.FlashPipeline.CrcParameters.Presets[mapped];
        }
    }

    /// <summary>Issue 3: 是否为自定义 CRC 参数 (SelectedCrcPresetIndex == CustomSentinel).</summary>
    public bool IsCrcCustom => _selectedCrcPresetIndex == CustomSentinel;
}

/// <summary>EcuReset (0x11) parameters.</summary>
public sealed record EcuResetParams
{
    public EcuResetType ResetType { get; set; } = EcuResetType.HardReset;
}

/// <summary>Checksum algorithm for Verify step.</summary>
public enum ChecksumAlgorithm { Crc32 = 1, Crc16 = 2, OemDefined = 3 }

/// <summary>ISO 14229 编程依赖性检查 parameters. 刷写完成后执行 0x31 RoutineControl 检查完整性+兼容性。</summary>
public sealed record DependencyCheckParams
{
    public ushort RoutineId { get; set; } = 0xFF01;  // 默认 0xFF01: 检查编程依赖性
}

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

/// <summary>Flash Driver Download parameters. Driver 下载到 RAM, ECU 自动执行擦写。</summary>
public sealed record FlashDriverDownloadParams
{
    // StartAddress 从 FlashDriver 解析出的 Segment.StartAddress 自动获取
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

    /// <summary>DependencyCheck parameters. Non-null only when Kind == DependencyCheck.</summary>
    public DependencyCheckParams? DependencyCheck { get; private set; }

    /// <summary>FlashDriverDownload parameters. Non-null only when Kind == FlashDriverDownload.</summary>
    public FlashDriverDownloadParams? FlashDriverDownload { get; private set; }

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

            case FlashStepKind.DependencyCheck:
                DependencyCheck = new DependencyCheckParams();
                break;

            case FlashStepKind.FlashDriverDownload:
                FlashDriverDownload = new FlashDriverDownloadParams();
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

    /// <summary>
    /// Issue 1: Erase 步骤选择 Segment 后, 自动用 Segment 的地址+大小更新 RoutineControl.
    /// 让 UI 文本框立即显示自动填充的值.
    /// </summary>
    internal void UpdateEraseAddressFromSegment(uint startAddress, uint size)
    {
        if (Kind != FlashStepKind.Erase || RoutineControl is null) return;
        RoutineControl.ApplySegmentAddress(startAddress, size);
    }
}
