using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

/// <summary>补全对话框里可勾选为 NM 的报文候选。</summary>
public sealed partial class NmMessageCandidateVm : ObservableObject
{
    public NmMessageCandidateVm(string canIdHex, string? name, bool isExtended, byte dlc, bool isSelected)
    {
        CanIdHex = canIdHex;
        Name = name ?? "";
        IsExtended = isExtended;
        Dlc = dlc;
        IsSelected = isSelected;
    }

    public string CanIdHex { get; }

    public string Name { get; }

    /// <summary>与 DBC 的 IDE bit 一致；匹配帧时必须同时比较扩展位。</summary>
    public bool IsExtended { get; }

    public int Dlc { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>样本帧里的一个字节；点选后由向导 VM 决定落到 NID 还是 CBV。</summary>
public sealed partial class NmSampleByteVm : ObservableObject
{
    public NmSampleByteVm(int index, byte value, Action<int> onSelect)
    {
        Index = index;
        Value = value;
        SelectCommand = new RelayCommand(() => onSelect(Index));
    }

    public int Index { get; }

    public byte Value { get; }

    public string HexText => Value.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>bit7…bit0（spec §9.2 的“按字节/位显示”）：CBV 的位角色靠它辨认。</summary>
    public string BitsText
    {
        get
        {
            Span<char> bits = stackalloc char[8];
            for (var bit = 0; bit < 8; bit++)
                bits[7 - bit] = (Value & (1 << bit)) != 0 ? '1' : '0';
            return new string(bits);
        }
    }

    [ObservableProperty] private bool _isSelected;

    public IRelayCommand SelectCommand { get; }
}

/// <summary>一帧 NM 样本：每帧一行，按字节渲染。</summary>
public sealed class NmSampleRowVm
{
    public NmSampleRowVm(string label, IReadOnlyList<NmSampleByteVm> bytes)
    {
        Label = label;
        Bytes = bytes;
    }

    public string Label { get; }

    public IReadOnlyList<NmSampleByteVm> Bytes { get; }
}

/// <summary>
/// 左侧缺项列表的一行：能力/原因/建议，以及 spec §9.2 要求的「影响」与「状态」。
/// 状态依据编辑器字段是否已有值，并在字段变化时刷新。
/// </summary>
public sealed partial class NmMissingItemVm : ObservableObject
{
    public NmMissingItemVm(NmInputMissing item, string impact, string status)
    {
        Item = item;
        Impact = impact;
        Status = status;
    }

    public NmInputMissing Item { get; }

    public string Capability => Item.Capability.ToString();

    public string Why => Item.Why;

    public string? Suggestion => Item.Suggestion;

    /// <summary>该缺项导致的后果（中文一句话，按能力映射）。</summary>
    public string Impact { get; }

    /// <summary>需输入 / 已填写 / 自动可用。</summary>
    [ObservableProperty] private string _status = "";
}

/// <summary>节点列表的一行：节点名只读，站地址可编辑（0–255）。</summary>
public sealed partial class NmNodeAddressVm : ObservableObject
{
    private readonly NmNodeDef _definition;

    public NmNodeAddressVm(NmNodeDef definition)
    {
        _definition = definition;
        Name = definition.Name;
        StationAddress = definition.StationAddress;
        InitialStationAddress = definition.StationAddress;
    }

    public string Name { get; }

    /// <summary>构造时的站地址（来自合并后的 profile/sidecar）；用于识别用户是否改过。</summary>
    public int? InitialStationAddress { get; }

    public bool IsStationAddressEdited => StationAddress != InitialStationAddress;

    [ObservableProperty] private int? _stationAddress;

    /// <summary>写回 sidecar 时只替换站地址，保留节点标识/NM 标记等既有字段。</summary>
    public NmNodeDef ToNodeDef()
    {
        if (StationAddress is not { } value)
            return _definition with { StationAddress = null };
        // 越界值绝不能静默变成 null；保存前的 Validate 已拦截，这里兜底报错。
        if (value is < 0 or > 255)
            throw new InvalidOperationException($"节点 {Name} 的站地址必须在 0–255");
        return _definition with { StationAddress = (byte)value };
    }
}

/// <summary>
/// 补全 NM 输入的对话框 VM。建议值只填充编辑器，永不自动生效；
/// 只有「保存到项目」才会写 sidecar。
/// </summary>
public sealed partial class NmInputWizardViewModel : ObservableObject
{
    private const int MaxSampleFrames = 3;

    // 建议文本是自由文本，只能按「字段在建议串里的固定形态」抽取数值。
    [GeneratedRegex(@"byte\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ByteIndexRegex();

    [GeneratedRegex(@"(-?\d+)\s*ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MillisecondsRegex();

    [GeneratedRegex(@"offset\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"length\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthRegex();

    private readonly NmToolSnapshot _snapshot;
    private readonly NmMessageDef? _firstNm;
    // 调用方可注入已解析的 DBC（测试/未来接线）；未注入时试算前按 DbcPath 懒解析。
    private readonly DbcDocument? _dbc;
    // 当前用于样本预览的候选：勾选中的第一个；无勾选时取候选列表第一个。
    private NmMessageCandidateVm? _selectedCandidate;
    private ReplayFrame? _sampleFrame;
    private IReadOnlyList<NmSampleRowVm> _sampleRows = Array.Empty<NmSampleRowVm>();
    private int? _selectedByteIndex;
    private string _trialSummary = "";
    // 导入的配置：只对文件中确实出现的字段视为「显式值」，缺失字段保留当前编辑器/旧 sidecar。
    private NmUserSupplement? _importedSupplement;
    private readonly HashSet<string> _importedFields = new(StringComparer.Ordinal);
    // 统计一次导入是否包含任何可识别的 NM 配置字段；空 JSON（如 {}）不得当成导入成功。
    private static bool HasRecognizableConfig(NmUserSupplement imported)
        => imported.NmMessageSelectors is { Count: > 0 }
           || imported.IdMapping is not null
           || imported.NidByte is not null
           || imported.CbvByte is not null
           || imported.UserDataOffset is not null
           || imported.UserDataLength is not null
           || imported.Timings is { } timings
              && (timings.CycleMs is not null || timings.TimeoutMs is not null
                  || timings.RepeatMessageMs is not null || timings.WaitBusSleepMs is not null
                  || timings.ImmediateCycleMs is not null || timings.ImmediateCount is not null)
           || imported.Features is { } features
              && (features.ActiveWakeupBitEnabled is not null
                  || features.RepeatMsgIndEnabled is not null
                  || features.PnEnabled is not null)
           || imported.Nodes is { Count: > 0 };
    // 磁盘上已有的 sidecar：保存时按字段合并，避免第二次补全覆盖掉第一轮的输入。
    private readonly NmUserSupplement? _existing;
    // 保存时允许回落的旧布局值；DBC 哈希不一致时置空，避免「警告了却仍写回旧值」。
    private readonly byte? _storedNidByte;
    private readonly byte? _storedCbvByte;
    private readonly byte? _storedUserDataOffset;
    private readonly int? _storedUserDataLength;
    // 编辑器预填值。记住它们才能区分「用户改过」与「只是回显」。
    private readonly byte? _initialNidByte;
    private readonly byte? _initialCbvByte;
    private readonly byte? _initialUserDataOffset;
    private readonly int? _initialUserDataLength;
    private readonly int? _initialCycleMs;
    private readonly int? _initialTimeoutMs;
    private readonly bool? _initialPnEnabled;

    [ObservableProperty] private byte? _nidByte;
    [ObservableProperty] private byte? _cbvByte;
    [ObservableProperty] private byte? _userDataOffset;
    [ObservableProperty] private int? _userDataLength;
    [ObservableProperty] private int? _cycleMs;
    [ObservableProperty] private int? _timeoutMs;
    [ObservableProperty] private bool? _pnEnabled;
    [ObservableProperty] private string _status = "";

    /// <summary>保存成功后触发；窗口据此关闭对话框（DialogResult = true）。</summary>
    public event EventHandler? RequestClose;

    public NmInputWizardViewModel(NmToolSnapshot snapshot, DbcDocument? dbc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _dbc = dbc;
        _firstNm = snapshot.Profile.Messages.FirstOrDefault(m => m.IsNm);

        _existing = snapshot.DbcPath is null ? null : NmSidecarStore.Load(snapshot.DbcPath);

        // sidecar 记录的 DBC 哈希与当前快照不一致：布局类输入来自旧 DBC，
        // 不能静默回填成「已确认」的值；计时/选择器仍按既有逻辑预填。
        var dbcChanged = _existing?.DbcHash is { Length: > 0 } existingHash
            && !string.Equals(existingHash, snapshot.DbcHash, StringComparison.Ordinal);

        // 已有 sidecar 的值回填编辑器：用户第二次补全时能看到并继续编辑上一轮的输入。
        // DBC 已变更时不预填布局类字段（NID/CBV/用户数据），要求用户重新确认。
        if (!dbcChanged)
        {
            NidByte = _existing?.NidByte;
            CbvByte = _existing?.CbvByte;
            UserDataOffset = _existing?.UserDataOffset;
            UserDataLength = _existing?.UserDataLength;
        }
        PnEnabled = _existing?.Features?.PnEnabled;
        CycleMs = _existing?.Timings?.CycleMs ?? snapshot.Profile.Timings.CycleMs;
        TimeoutMs = _existing?.Timings?.TimeoutMs ?? snapshot.Profile.Timings.TimeoutMs;

        if (dbcChanged)
            Status = "DBC 已变更：以下输入来自旧 DBC，请重新确认后再保存";

        // DBC 已变更时旧布局不参与保存合并：用户不重新输入就不会把旧 DBC 的值写回去。
        _storedNidByte = dbcChanged ? null : _existing?.NidByte;
        _storedCbvByte = dbcChanged ? null : _existing?.CbvByte;
        _storedUserDataOffset = dbcChanged ? null : _existing?.UserDataOffset;
        _storedUserDataLength = dbcChanged ? null : _existing?.UserDataLength;

        _initialNidByte = NidByte;
        _initialCbvByte = CbvByte;
        _initialUserDataOffset = UserDataOffset;
        _initialUserDataLength = UserDataLength;
        _initialCycleMs = CycleMs;
        _initialTimeoutMs = TimeoutMs;
        _initialPnEnabled = PnEnabled;

        // 节点初值来自合并后的 profile（profile 已吸收 sidecar 的 Nodes）。
        NodeAddresses = new ObservableCollection<NmNodeAddressVm>();
        foreach (var node in snapshot.Profile.Nodes)
            AddNodeAddress(new NmNodeAddressVm(node));

        MessageCandidates = new ObservableCollection<NmMessageCandidateVm>(
            snapshot.Profile.Messages
                // 真实链路里 profile 已被 NmSupplementMerger 合并：上次勾选的报文 IsNm 已为 true，
                // 但 Reasons 带 UserSelected，必须仍作为候选出现，否则无法预勾选/取消勾选。
                .Where(m => !m.IsNm || m.Reasons.Contains("UserSelected"))
                .Select(m => new NmMessageCandidateVm(
                    m.CanIdHex,
                    m.Name,
                    m.IsExtended,
                    m.Dlc,
                    m.Reasons.Contains("UserSelected")
                    || (_existing?.NmMessageSelectors?.Contains(
                        m.CanIdHex, StringComparer.OrdinalIgnoreCase) == true))));

        // 勾选变化会改变样本预览与 DLC（校验依赖 DLC），逐个订阅并立即算一次。
        foreach (var candidate in MessageCandidates)
            candidate.PropertyChanged += OnCandidatePropertyChanged;
        RefreshSample();

        // 缺项行必须在样本/DLC 就绪后生成：状态里的「NID 字节 < DLC」依赖 Dlc。
        MissingItems = snapshot.Report.MissingInputs
            .Select(m => new NmMissingItemVm(m, BuildImpact(m), BuildStatus(m)))
            .ToArray();
    }

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NmMessageCandidateVm.IsSelected))
        {
            RefreshSample();
            RefreshMissingStatuses();
        }
    }

    private void OnNodeAddressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NmNodeAddressVm.StationAddress))
            RefreshMissingStatuses();
    }

    /// <summary>spec §9.2：缺项影响按能力映射成一句中文后果说明。</summary>
    private static string BuildImpact(NmInputMissing item) => item.Capability switch
    {
        NmCapability.IdentifyMessages => "无法识别 NM 报文，周期/活动分析不可用",
        NmCapability.Timing => item.InputKey == "cycle_ms"
            ? "无法判定周期偏差"
            : "无法判定超时静默与漏帧",
        NmCapability.NidCheck => item.InputKey == "nm_node_mapping"
            ? "无法比对 NID 与节点站地址"
            : "无法判定 NID 不符/未知节点",
        NmCapability.CbvDecode => "无法解码 CBV 控制位",
        NmCapability.PnCheck => "无法判定 PN/部分网络状态",
        NmCapability.UserData => "无法判定用户数据长度/内容",
        _ => "对应能力不可用",
    };

    /// <summary>spec §9.2：状态 = 需输入 / 已填写 / 自动可用。</summary>
    private string BuildStatus(NmInputMissing item) => item.InputKey switch
    {
        "nm_message_selection" => MessageCandidates.Any(c => c.IsSelected) ? "已填写" : "需输入",
        "nid_byte" => NidByte is { } nid
            ? nid < Dlc ? "已填写" : "需输入"
            : AutoAvailable(item),
        "nm_node_mapping" => NodeAddresses.Any(n => n.StationAddress is not null)
            ? "已填写" : "需输入",
        "cbv_byte" => CbvByte is { } cbv
            ? cbv < Dlc ? "已填写" : "需输入"
            : AutoAvailable(item),
        "cycle_ms" => CycleMs is not null ? "已填写" : AutoAvailable(item),
        "timeout_ms" => TimeoutMs is not null ? "已填写" : AutoAvailable(item),
        "pn_enabled" => PnEnabled is not null ? "已填写" : "需输入",
        "user_data_range" => UserDataOffset is not null && UserDataLength is not null
            ? "已填写" : AutoAvailable(item),
        _ => "需输入",
    };

    /// <summary>有确定性建议（实测/假设）时标「自动可用」，否则仍需人工输入。</summary>
    private static string AutoAvailable(NmInputMissing item)
        => item.SuggestionSource is "measured" or "assumption" ? "自动可用" : "需输入";

    private void RefreshMissingStatuses()
    {
        foreach (var item in MissingItems)
            item.Status = BuildStatus(item.Item);
    }

    /// <summary>
    /// 重算当前候选与样本帧：优先勾选中的候选，否则取候选列表第一个；
    /// 完全没有候选（DBC 已完整标记 NM）时回退到第一条 NM 报文。
    /// </summary>
    private void RefreshSample()
    {
        _selectedCandidate = MessageCandidates.FirstOrDefault(c => c.IsSelected)
            ?? MessageCandidates.FirstOrDefault();

        var frames = _selectedCandidate is { } candidate
            ? FindFrames(candidate.CanIdHex, candidate.IsExtended)
            : _firstNm is { } nm
                ? FindFrames(nm.CanIdHex, nm.IsExtended)
                : Array.Empty<ReplayFrame>();

        _sampleFrame = frames.Count > 0 ? frames[0] : null;
        _sampleRows = BuildSampleRows(frames);
        ApplyByteSelection();

        OnPropertyChanged(nameof(SampleRows));
        OnPropertyChanged(nameof(SampleBytes));
        OnPropertyChanged(nameof(SampleByteTexts));
        OnPropertyChanged(nameof(Dlc));
        OnPropertyChanged(nameof(SelectedByteDecode));
    }

    /// <summary>同一 NM 候选取前 3 帧，按时间排序；预览每帧一行。</summary>
    private IReadOnlyList<ReplayFrame> FindFrames(string canIdHex, bool isExtended)
    {
        var id = ParseId(canIdHex);
        return _snapshot.Frames
            .Where(f => (f.Id & 0x1FFFFFFFu) == id && f.IsExtended == isExtended)
            .OrderBy(f => f.Timestamp)
            .Take(MaxSampleFrames)
            .ToArray();
    }

    private IReadOnlyList<NmSampleRowVm> BuildSampleRows(IReadOnlyList<ReplayFrame> frames)
    {
        var rows = new List<NmSampleRowVm>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var bytes = new NmSampleByteVm[frame.Data.Length];
            for (var b = 0; b < bytes.Length; b++)
                bytes[b] = new NmSampleByteVm(b, frame.Data[b], SelectByte);
            rows.Add(new NmSampleRowVm(
                FormattableString.Invariant($"#{i + 1} t={frame.Timestamp:F3}s"), bytes));
        }
        return rows;
    }

    /// <summary>
    /// 点击样本字节：NID 字节为空时设为 NID，否则设为 CBV。
    /// 越界（负值或 ≥ DLC）不赋值，避免把非法索引带进 sidecar。
    /// </summary>
    public void SelectByte(int index)
    {
        if (index < 0 || index >= Dlc) return;

        SelectedByteIndex = index;
        if (NidByte is null) NidByte = (byte)index;
        else CbvByte = (byte)index;
        ApplyByteSelection();
        OnPropertyChanged(nameof(SelectedByteDecode));
    }

    private void ApplyByteSelection()
    {
        foreach (var row in _sampleRows)
            foreach (var b in row.Bytes)
                b.IsSelected = b.Index == _selectedByteIndex;
    }

    // 字段被直接编辑时，右侧解码行与左侧缺项状态都要跟着变。
    partial void OnNidByteChanged(byte? value)
    {
        OnPropertyChanged(nameof(SelectedByteDecode));
        RefreshMissingStatuses();
    }

    partial void OnCbvByteChanged(byte? value)
    {
        OnPropertyChanged(nameof(SelectedByteDecode));
        RefreshMissingStatuses();
    }

    partial void OnCycleMsChanged(int? value) => RefreshMissingStatuses();

    partial void OnTimeoutMsChanged(int? value) => RefreshMissingStatuses();

    partial void OnPnEnabledChanged(bool? value) => RefreshMissingStatuses();

    partial void OnUserDataOffsetChanged(byte? value) => RefreshMissingStatuses();

    partial void OnUserDataLengthChanged(int? value) => RefreshMissingStatuses();

    public IReadOnlyList<NmMissingItemVm> MissingItems { get; private set; }
        = Array.Empty<NmMissingItemVm>();

    /// <summary>DBC 里尚未标记为 NM 的报文；勾选后会进入 sidecar 的 nm_message_selectors。</summary>
    public ObservableCollection<NmMessageCandidateVm> MessageCandidates { get; }

    /// <summary>节点站地址编辑行；保存时写回 sidecar 的 Nodes。</summary>
    public ObservableCollection<NmNodeAddressVm> NodeAddresses { get; }

    /// <summary>当前 trace 中同一 NM 候选的 1–3 帧样本（每帧一行）。</summary>
    public IReadOnlyList<NmSampleRowVm> SampleRows => _sampleRows;

    /// <summary>当前点选的字节索引；未点选时为空。</summary>
    public int? SelectedByteIndex
    {
        get => _selectedByteIndex;
        private set
        {
            if (_selectedByteIndex == value) return;
            _selectedByteIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>右侧实时解码：byteN = 0xXX（NID/CBV/未分配）。</summary>
    public string SelectedByteDecode
    {
        get
        {
            if (_selectedByteIndex is not { } index) return "点击任一字节可设为 NID/CBV";
            var value = _sampleFrame is { } frame && index < frame.Data.Length
                ? frame.Data[index].ToString("X2", CultureInfo.InvariantCulture)
                : "--";
            var role = NidByte == index ? "NID" : CbvByte == index ? "CBV" : "未分配";
            return FormattableString.Invariant($"byte{index} = 0x{value}（{role}）");
        }
    }

    /// <summary>取当前 trace 里所选候选报文的第一帧字节；没有样本时为空。</summary>
    public IReadOnlyList<byte> SampleBytes => _sampleFrame?.Data ?? Array.Empty<byte>();

    /// <summary>样本预览的十六进制文本（每字节两位大写 hex）。</summary>
    public IReadOnlyList<string> SampleByteTexts
        => SampleBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)).ToArray();

    /// <summary>校验用的 DLC：优先实测样本，其次所选候选，最后回落到 8。</summary>
    public int Dlc => _sampleFrame?.Dlc ?? _selectedCandidate?.Dlc ?? _firstNm?.Dlc ?? 8;

    /// <summary>最近一次试算的展示文本（识别数、周期中位、NID 分布、空档、突发）。</summary>
    public string TrialSummary => _trialSummary;

    /// <summary>
    /// 导出文件选择委托：参数为建议文件名，返回目标路径；null 表示用户取消。
    /// 测试注入临时路径，避免弹窗；窗口在构造时接真实对话框。
    /// </summary>
    public Func<string, string?>? SaveFilePicker { get; set; }

    /// <summary>导入文件选择委托：返回选中路径；null 表示用户取消。</summary>
    public Func<string?>? OpenFilePicker { get; set; }

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (CycleMs is { } cycle && cycle <= 0) errors.Add("周期必须大于 0");
        if (NidByte is { } nid && nid >= Dlc) errors.Add("NID 字节必须小于 DLC");
        if (CbvByte is { } cbv && cbv >= Dlc) errors.Add("CBV 字节必须小于 DLC");
        if (NidByte is { } n && CbvByte is { } c && n == c) errors.Add("NID 与 CBV 字节不能相同");
        // offset 单独填写也必须落在 DLC 内；不要求 length 同时存在。
        if (UserDataOffset is { } off)
        {
            if (off >= Dlc) errors.Add("用户数据 offset 必须小于 DLC");
            else if (UserDataLength is { } len && off + len > Dlc)
                errors.Add("用户数据区间超出 DLC");
        }
        if (UserDataLength is { } negativeLength && negativeLength < 0)
            errors.Add("用户数据 length 不能为负");
        foreach (var node in NodeAddresses)
        {
            if (node.StationAddress is { } station && station is < 0 or > 255)
                errors.Add($"节点 {node.Name} 的站地址必须在 0–255");
        }
        // spec §6.6.5 写 timeout > cycle，§9.2 写 timeout ≥ cycle；冲突时取严格侧：
        // 等于也拒绝，避免把「超时等于周期」当成可观测的超时窗口。
        // 周期框被清空时，sidecar 会回落到 DBC/合并 profile 的周期，校验必须用生效值，
        // 否则会写出 timeout < 实际 cycle 的组合，把正常周期全部判成空档。
        var effectiveCycle = CycleMs ?? _snapshot.Profile.Timings.CycleMs ?? _firstNm?.CycleMs;
        if (TimeoutMs is { } timeout && effectiveCycle is { } effective && timeout <= effective)
            errors.Add("超时必须大于周期");
        return errors;
    }

    public NmUserSupplement ToSupplement() => new(
        NmMessageSelectors: BuildSelectors(),
        // 导入的 mapping 优先于目标项目既有的 sidecar（编辑器里没有该字段）。
        IdMapping: _importedSupplement?.IdMapping ?? _existing?.IdMapping,
        NidByte: Pick(NidByte, _initialNidByte, _storedNidByte, _importedFields.Contains("nid_byte")),
        CbvByte: Pick(CbvByte, _initialCbvByte, _storedCbvByte, _importedFields.Contains("cbv_byte")),
        UserDataOffset: Pick(UserDataOffset, _initialUserDataOffset, _storedUserDataOffset,
            _importedFields.Contains("user_data_offset")),
        UserDataLength: Pick(UserDataLength, _initialUserDataLength, _storedUserDataLength,
            _importedFields.Contains("user_data_length")),
        Timings: BuildTimings(),
        Features: BuildFeatures(),
        Nodes: BuildNodes(),
        DbcPath: _snapshot.DbcPath,
        DbcHash: _snapshot.DbcHash);

    /// <summary>
    /// 逐字段合并：导入/用户改过的值优先，未改动的字段沿用既有 sidecar。
    /// 否则整文件覆盖会把上一轮保存的字段静默抹掉。
    /// </summary>
    private static T Pick<T>(T current, T initial, T stored, bool imported)
        => imported || !EqualityComparer<T>.Default.Equals(current, initial) ? current : stored;

    /// <summary>
    /// 只有用户真正改过站地址（或导入了节点）才把节点写进 sidecar；
    /// 纯 DBC 节点不冻结成用户补充输入。一旦写回，因 merger 对 Nodes 是整体替换，
    /// 必须写当前完整节点表，否则其余节点会从分析 profile 里消失。
    /// </summary>
    private IReadOnlyList<NmNodeDef>? BuildNodes()
    {
        if (NodeAddresses.Count > 0
            && (NodeAddresses.Any(n => n.IsStationAddressEdited)
                || _importedSupplement?.Nodes is { Count: > 0 }))
        {
            return NodeAddresses.Select(n => n.ToNodeDef()).ToArray();
        }
        return _existing?.Nodes;
    }

    /// <summary>只回传用户改过的计时字段，未改动的沿用既有 sidecar（其余计时字段原样保留）。</summary>
    private NmTimings? BuildTimings()
    {
        var stored = _existing?.Timings;
        var imported = _importedSupplement?.Timings;
        var cycle = Pick(CycleMs, _initialCycleMs, stored?.CycleMs, _importedFields.Contains("cycle_ms"));
        var timeout = Pick(TimeoutMs, _initialTimeoutMs, stored?.TimeoutMs,
            _importedFields.Contains("timeout_ms"));
        if (cycle is null && timeout is null && stored is null && imported is null) return null;
        return new NmTimings(
            CycleMs: cycle,
            TimeoutMs: timeout,
            RepeatMessageMs: imported?.RepeatMessageMs ?? stored?.RepeatMessageMs,
            WaitBusSleepMs: imported?.WaitBusSleepMs ?? stored?.WaitBusSleepMs,
            ImmediateCycleMs: imported?.ImmediateCycleMs ?? stored?.ImmediateCycleMs,
            ImmediateCount: imported?.ImmediateCount ?? stored?.ImmediateCount);
    }

    /// <summary>PN 勾选同理；导入/既有 sidecar 的其他 feature 字段原样保留。</summary>
    private NmFeatures? BuildFeatures()
    {
        var stored = _existing?.Features;
        var imported = _importedSupplement?.Features;
        var pn = Pick(PnEnabled, _initialPnEnabled, stored?.PnEnabled, _importedFields.Contains("pn_enabled"));
        if (pn is null && stored is null && imported is null) return null;
        return new NmFeatures(
            ActiveWakeupBitEnabled: imported?.ActiveWakeupBitEnabled ?? stored?.ActiveWakeupBitEnabled,
            RepeatMsgIndEnabled: imported?.RepeatMsgIndEnabled ?? stored?.RepeatMsgIndEnabled,
            PnEnabled: pn);
    }

    /// <summary>
    /// 选择器由当前勾选派生，仅保留 DBC 里不存在（候选列表无法展示）的既有选择器。
    /// 这样取消勾选既有报文会真正把它从 sidecar 移除，而不是被并集永久保留。
    /// </summary>
    private IReadOnlyList<string>? BuildSelectors()
    {
        var checkedIds = MessageCandidates
            .Where(c => c.IsSelected)
            .Select(c => c.CanIdHex)
            .ToArray();
        // 导入文件里不在当前 DBC 候选中的选择器也要保留，避免导入后被静默丢弃。
        var unmatched = (_existing?.NmMessageSelectors ?? Array.Empty<string>())
            .Concat(_importedSupplement?.NmMessageSelectors ?? Array.Empty<string>())
            .Where(id => !MessageCandidates.Any(c =>
                string.Equals(c.CanIdHex, id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectors = checkedIds
            .Concat(unmatched)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return selectors.Length == 0 ? null : selectors;
    }

    [RelayCommand]
    private async Task TrialAsync()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = string.Join("；", errors);
            return;
        }

        try
        {
            var profile = NmSupplementMerger.Merge(_snapshot.Profile, ToSupplement());
            var end = _snapshot.Report.Window.End;
            // 试算必须带上真实 DBC：SignalDecode 等能力依赖它，不能传 null。
            var (dbc, dbcNote) = await Task.Run(LoadDbcForTrial);
            var report = await Task.Run(() => NmAnalyzer.Analyze(new NmAnalysisRequest(
                _snapshot.SourceId, _snapshot.Channel, _snapshot.Frames,
                dbc, profile, _snapshot.Report.Window.Start, end, "trial")));
            _trialSummary = BuildTrialSummary(report, profile, dbcNote);
            OnPropertyChanged(nameof(TrialSummary));
            Status = "试算完成";
        }
        catch (Exception ex)
        {
            // 试算失败必须在对话框里可见，不能吞成「无结果」。
            Status = "试算失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 懒解析 DBC：优先用调用方注入的实例，否则从快照路径读取。
    /// 解析/读取失败写进试算展示，不静默当成「无 DBC」。
    /// </summary>
    private (DbcDocument? Dbc, string Note) LoadDbcForTrial()
    {
        if (_dbc is not null) return (_dbc, "DBC：已加载");

        var path = _snapshot.DbcPath;
        if (string.IsNullOrEmpty(path)) return (null, "DBC：未使用（没有 DBC 路径）");
        if (!File.Exists(path)) return (null, "DBC：未使用（文件不存在）");

        try
        {
            var result = DbcParser.Parse(File.ReadAllText(path));
            return result.IsSuccess
                ? (result.Value, "DBC：已加载")
                : (null, "DBC：解析失败（" + result.Error!.Message + "）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "DBC：读取失败（" + ex.Message + "）");
        }
    }

    private string BuildTrialSummary(NmReport report, NmProfile profile, string dbcNote)
    {
        var lines = new List<string>
        {
            $"试算：识别 {report.Messages.Count} 条报文；" +
            $"能力 {report.Capabilities.Count(c => c.Available)} 项可用",
        };

        foreach (var message in report.Messages.Take(5))
        {
            var median = message.Cycle.MedianMs is { } value
                ? FormattableString.Invariant($"{value:F1} ms")
                : "样本不足";
            lines.Add(FormattableString.Invariant(
                $"  {message.CanIdHex} {message.Name ?? "—"}：周期中位 {median}"));
        }
        if (report.Messages.Count > 5) lines.Add("  …（仅显示前 5 条）");

        if (profile.Layout?.NidByte is { } nidByte)
        {
            var distribution = BuildNidDistribution(profile, nidByte);
            lines.Add(distribution is null
                ? $"NID(byte{nidByte}) 分布：无样本"
                : $"NID(byte{nidByte}) 分布：{distribution}");
        }

        // 空档 = 全部/单节点/全网静默事件；观察突发 = 内容分析器在静默后观察到的短间隔序列
        // （burst_observed）。不能用 Activity 的 wakeup 段：那是任何持续报文都会有的唤醒观察窗。
        var gapCount = report.Events.Count(e =>
            e.Type is "all_nm_silent" or "sender_silent" or "bus_wide_silence");
        lines.Add($"空档 {gapCount} 条");
        var burstCount = report.Events.Count(e => e.Type == "burst_observed");
        lines.Add($"观察突发 {burstCount} 条");
        lines.Add(dbcNote);
        return string.Join(Environment.NewLine, lines);
    }

    private string? BuildNidDistribution(NmProfile profile, byte nidByte)
    {
        var nm = profile.Messages.FirstOrDefault(m => m.IsNm);
        if (nm is null) return null;

        var id = ParseId(nm.CanIdHex);
        var values = _snapshot.Frames
            .Where(f => (f.Id & 0x1FFFFFFFu) == id
                        && f.IsExtended == nm.IsExtended
                        && f.Data.Length > nidByte)
            .GroupBy(f => f.Data[nidByte])
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => FormattableString.Invariant($"0x{g.Key:X2}×{g.Count()}"))
            .ToArray();
        return values.Length == 0 ? null : string.Join(", ", values);
    }

    /// <summary>左侧列表「采用建议」命令：参数是缺项行 VM。</summary>
    [RelayCommand]
    private void AdoptMissingItem(NmMissingItemVm? item) => AdoptSuggestion(item?.Item);

    /// <summary>
    /// 采用左侧缺项的建议：按 InputKey 决定填哪个字段，从建议串里抽数值。
    /// 解析不了的值保持原样，并在状态栏要求用户手动确认（建议永不自动生效）。
    /// </summary>
    public void AdoptSuggestion(NmInputMissing? item)
    {
        if (item?.Suggestion is not { Length: > 0 } suggestion)
        {
            Status = "该建议需手动确认";
            return;
        }

        switch (item.InputKey)
        {
            case "nid_byte" when TryParseByteIndex(suggestion, out var nid):
                NidByte = (byte)nid;
                Status = $"已采用建议：NID 字节 = {nid}";
                break;
            case "cbv_byte" when TryParseByteIndex(suggestion, out var cbv):
                CbvByte = (byte)cbv;
                Status = $"已采用建议：CBV 字节 = {cbv}";
                break;
            case "cycle_ms" when TryParseMilliseconds(suggestion, out var cycle):
                CycleMs = cycle;
                Status = $"已采用建议：周期 = {cycle} ms";
                break;
            case "timeout_ms" when TryParseMilliseconds(suggestion, out var timeout):
                TimeoutMs = timeout;
                Status = $"已采用建议：超时 = {timeout} ms";
                break;
            case "user_data_range" when TryParseUserData(suggestion, out var offset, out var length):
                UserDataOffset = (byte)offset;
                UserDataLength = length;
                Status = $"已采用建议：用户数据 offset {offset}、length {length}";
                break;
            default:
                Status = "该建议需手动确认";
                break;
        }
    }

    private static bool TryParseByteIndex(string text, out int index)
    {
        index = -1;
        var match = ByteIndexRegex().Match(text);
        return match.Success && int.TryParse(
            match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && index is >= 0 and < 8;
    }

    private static bool TryParseMilliseconds(string text, out int milliseconds)
    {
        milliseconds = -1;
        var match = MillisecondsRegex().Match(text);
        return match.Success && int.TryParse(
            match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out milliseconds) && milliseconds > 0;
    }

    private static bool TryParseUserData(string text, out int offset, out int length)
    {
        offset = -1;
        length = -1;
        var offsetMatch = OffsetRegex().Match(text);
        var lengthMatch = LengthRegex().Match(text);
        if (!offsetMatch.Success || !lengthMatch.Success) return false;
        return int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out offset)
               && int.TryParse(lengthMatch.Groups[1].Value, NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out length)
               && offset >= 0 && length >= 0;
    }

    /// <summary>导出当前编辑器内容为任意路径的 `*.nmprofile.json`（不写项目 sidecar）。</summary>
    [RelayCommand]
    private void ExportProfile()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = string.Join("；", errors);
            return;
        }
        if (SaveFilePicker is null)
        {
            Status = "未配置导出文件选择器";
            return;
        }

        var suggested = _snapshot.DbcPath is { } dbcPath
            ? Path.GetFileNameWithoutExtension(dbcPath) + ".nmprofile.json"
            : "nm.nmprofile.json";
        var path = SaveFilePicker(suggested);
        if (string.IsNullOrWhiteSpace(path)) return; // 用户取消选择。

        try
        {
            NmSidecarStore.SaveTo(path, ToSupplement());
            Status = "已导出到 " + path;
        }
        catch (Exception ex)
        {
            // 导出失败（无权限/目录只读）必须回报。
            Status = "导出失败：" + ex.Message;
        }
    }

    /// <summary>导入 `*.nmprofile.json` 回填编辑器；保留校验，不自动保存。</summary>
    [RelayCommand]
    private void ImportProfile()
    {
        if (OpenFilePicker is null)
        {
            Status = "未配置导入文件选择器";
            return;
        }

        var path = OpenFilePicker();
        if (string.IsNullOrWhiteSpace(path)) return; // 用户取消选择。

        try
        {
            var imported = NmSidecarStore.LoadFrom(path);
            if (imported is null)
            {
                Status = "导入失败：不是可解析的 nmprofile 文件";
                return;
            }
            if (!HasRecognizableConfig(imported))
            {
                // 合法 JSON 但没有一个已知字段（如 {}）不能当成「导入成功」，
                // 否则保存时会把空配置写进 sidecar。
                Status = "导入失败：文件不包含可识别的 NM 配置";
                return;
            }

            ApplyImported(imported);
            var errors = Validate();
            Status = errors.Count > 0
                ? "已导入配置，但校验未通过：" + string.Join("；", errors) + "；尚未保存"
                : "已导入配置（尚未保存）";
        }
        catch (Exception ex)
        {
            Status = "导入失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 只回填导入文件中实际出现的字段：缺失字段保留编辑器当前值，
    /// 避免「导入只含周期的配置」把目标项目已有的 NID/CBV 清空。
    /// 导入的非编辑器字段（IdMapping/计时扩展/feature 标志）单独保存，供保存时透传。
    /// </summary>
    private void ApplyImported(NmUserSupplement imported)
    {
        _importedSupplement = imported;
        _importedFields.Clear();

        if (imported.NidByte is { } nid)
        {
            NidByte = nid;
            _importedFields.Add("nid_byte");
        }
        if (imported.CbvByte is { } cbv)
        {
            CbvByte = cbv;
            _importedFields.Add("cbv_byte");
        }
        if (imported.UserDataOffset is { } offset)
        {
            UserDataOffset = offset;
            _importedFields.Add("user_data_offset");
        }
        if (imported.UserDataLength is { } length)
        {
            UserDataLength = length;
            _importedFields.Add("user_data_length");
        }
        if (imported.Timings?.CycleMs is { } cycle)
        {
            CycleMs = cycle;
            _importedFields.Add("cycle_ms");
        }
        if (imported.Timings?.TimeoutMs is { } timeout)
        {
            TimeoutMs = timeout;
            _importedFields.Add("timeout_ms");
        }
        if (imported.Features?.PnEnabled is { } pn)
        {
            PnEnabled = pn;
            _importedFields.Add("pn_enabled");
        }

        if (imported.NmMessageSelectors is { } selectors)
        {
            var selected = new HashSet<string>(selectors, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in MessageCandidates)
                candidate.IsSelected = selected.Contains(candidate.CanIdHex);
        }

        if (imported.Nodes is { Count: > 0 } nodes)
        {
            // 导入的节点在本轮保存时必须写回（BuildNodes 依据 _importedSupplement 判断）。
            NodeAddresses.Clear();
            foreach (var node in nodes)
                AddNodeAddress(new NmNodeAddressVm(node));
            // 新行的站地址可能直接补上 nm_node_mapping；必须重新计算左侧状态。
            RefreshMissingStatuses();
        }
    }

    /// <summary>加入节点行并订阅站地址变化（导入会替换整个集合，不能只靠构造期订阅）。</summary>
    private void AddNodeAddress(NmNodeAddressVm node)
    {
        node.PropertyChanged += OnNodeAddressChanged;
        NodeAddresses.Add(node);
    }

    [RelayCommand]
    private void Save()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = string.Join("；", errors);
            return;
        }
        if (_snapshot.DbcPath is null)
        {
            Status = "没有 DBC 路径，无法保存 sidecar";
            return;
        }

        try
        {
            NmSidecarStore.Save(_snapshot.DbcPath, ToSupplement());
            Status = "已保存到 " + NmSidecarStore.PathFor(_snapshot.DbcPath) + "；关闭对话框后面板将重算";
            // 保存成功自动关闭：面板由 OpenNmWizardAsync 在 ShowDialog 返回后重算。
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 写 sidecar 失败（无权限/目录只读）必须回报给用户。
            Status = "保存失败：" + ex.Message;
        }
    }
}
