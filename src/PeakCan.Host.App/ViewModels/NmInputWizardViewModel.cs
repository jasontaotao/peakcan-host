using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>
/// 补全 NM 输入的对话框 VM。建议值只填充编辑器，永不自动生效；
/// 只有「保存到项目」才会写 sidecar。
/// </summary>
public sealed partial class NmInputWizardViewModel : ObservableObject
{
    private readonly NmToolSnapshot _snapshot;
    private readonly NmMessageDef? _firstNm;
    // 当前用于样本预览的候选：勾选中的第一个；无勾选时取候选列表第一个。
    private NmMessageCandidateVm? _selectedCandidate;
    private ReplayFrame? _sampleFrame;
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

    public NmInputWizardViewModel(NmToolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
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

        MissingItems = snapshot.Report.MissingInputs.ToArray();

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
    }

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NmMessageCandidateVm.IsSelected))
            RefreshSample();
    }

    /// <summary>
    /// 重算当前候选与样本帧：优先勾选中的候选，否则取候选列表第一个；
    /// 完全没有候选（DBC 已完整标记 NM）时回退到第一条 NM 报文。
    /// </summary>
    private void RefreshSample()
    {
        _selectedCandidate = MessageCandidates.FirstOrDefault(c => c.IsSelected)
            ?? MessageCandidates.FirstOrDefault();

        _sampleFrame = _selectedCandidate is { } candidate
            ? FindFrame(candidate.CanIdHex, candidate.IsExtended)
            : _firstNm is { } nm
                ? FindFrame(nm.CanIdHex, nm.IsExtended)
                : null;

        OnPropertyChanged(nameof(SampleBytes));
        OnPropertyChanged(nameof(SampleByteTexts));
        OnPropertyChanged(nameof(Dlc));
    }

    private ReplayFrame? FindFrame(string canIdHex, bool isExtended)
    {
        var id = ParseId(canIdHex);
        return _snapshot.Frames.FirstOrDefault(f =>
            (f.Id & 0x1FFFFFFFu) == id && f.IsExtended == isExtended);
    }

    public IReadOnlyList<NmInputMissing> MissingItems { get; }

    /// <summary>DBC 里尚未标记为 NM 的报文；勾选后会进入 sidecar 的 nm_message_selectors。</summary>
    public ObservableCollection<NmMessageCandidateVm> MessageCandidates { get; }

    /// <summary>取当前 trace 里所选候选报文的第一帧字节；没有样本时为空。</summary>
    public IReadOnlyList<byte> SampleBytes => _sampleFrame?.Data ?? Array.Empty<byte>();

    /// <summary>样本预览的十六进制文本（每字节两位大写 hex）。</summary>
    public IReadOnlyList<string> SampleByteTexts
        => SampleBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)).ToArray();

    /// <summary>校验用的 DLC：优先实测样本，其次所选候选，最后回落到 8。</summary>
    public int Dlc => _sampleFrame?.Dlc ?? _selectedCandidate?.Dlc ?? _firstNm?.Dlc ?? 8;

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (NidByte is { } nid && nid >= Dlc) errors.Add("NID 字节必须小于 DLC");
        if (CbvByte is { } cbv && cbv >= Dlc) errors.Add("CBV 字节必须小于 DLC");
        if (NidByte is { } n && CbvByte is { } c && n == c) errors.Add("NID 与 CBV 字节不能相同");
        if (UserDataOffset is { } off && UserDataLength is { } len && off + len > Dlc)
            errors.Add("用户数据区间超出 DLC");
        if (TimeoutMs is { } timeout && CycleMs is { } cycle && timeout <= cycle)
            errors.Add("超时必须大于周期");
        return errors;
    }

    public NmUserSupplement ToSupplement() => new(
        NmMessageSelectors: BuildSelectors(),
        IdMapping: _existing?.IdMapping,
        NidByte: Pick(NidByte, _initialNidByte, _storedNidByte),
        CbvByte: Pick(CbvByte, _initialCbvByte, _storedCbvByte),
        UserDataOffset: Pick(UserDataOffset, _initialUserDataOffset, _storedUserDataOffset),
        UserDataLength: Pick(UserDataLength, _initialUserDataLength, _storedUserDataLength),
        Timings: BuildTimings(),
        Features: BuildFeatures(),
        Nodes: _existing?.Nodes,
        DbcPath: _snapshot.DbcPath,
        DbcHash: _snapshot.DbcHash);

    /// <summary>
    /// 逐字段合并：编辑器改过的值优先，未改动的字段沿用既有 sidecar。
    /// 否则整文件覆盖会把上一轮保存的字段静默抹掉。
    /// </summary>
    private static T Pick<T>(T current, T initial, T stored)
        => EqualityComparer<T>.Default.Equals(current, initial) ? stored : current;

    /// <summary>只回传用户改过的计时字段，未改动的沿用既有 sidecar（其余计时字段原样保留）。</summary>
    private NmTimings? BuildTimings()
    {
        var stored = _existing?.Timings;
        var cycle = Pick(CycleMs, _initialCycleMs, stored?.CycleMs);
        var timeout = Pick(TimeoutMs, _initialTimeoutMs, stored?.TimeoutMs);
        if (cycle is null && timeout is null && stored is null) return null;
        return new NmTimings(
            CycleMs: cycle,
            TimeoutMs: timeout,
            RepeatMessageMs: stored?.RepeatMessageMs,
            WaitBusSleepMs: stored?.WaitBusSleepMs,
            ImmediateCycleMs: stored?.ImmediateCycleMs,
            ImmediateCount: stored?.ImmediateCount);
    }

    /// <summary>PN 勾选同理；既有 sidecar 的其他 feature 字段原样保留。</summary>
    private NmFeatures? BuildFeatures()
    {
        var stored = _existing?.Features;
        var pn = Pick(PnEnabled, _initialPnEnabled, stored?.PnEnabled);
        if (pn is null && stored is null) return null;
        return new NmFeatures(
            ActiveWakeupBitEnabled: stored?.ActiveWakeupBitEnabled,
            RepeatMsgIndEnabled: stored?.RepeatMsgIndEnabled,
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
        var unmatched = _existing?.NmMessageSelectors?
            .Where(id => !MessageCandidates.Any(c =>
                string.Equals(c.CanIdHex, id, StringComparison.OrdinalIgnoreCase)))
            .ToArray() ?? Array.Empty<string>();
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
            var report = await Task.Run(() => NmAnalyzer.Analyze(new NmAnalysisRequest(
                _snapshot.SourceId, _snapshot.Channel, _snapshot.Frames,
                null, profile, _snapshot.Report.Window.Start, end, "trial")));
            Status = $"试算：识别 {report.Messages.Count} 条报文；" +
                     $"能力 {report.Capabilities.Count(c => c.Available)} 项可用";
        }
        catch (Exception ex)
        {
            // 试算失败必须在对话框里可见，不能吞成「无结果」。
            Status = "试算失败：" + ex.Message;
        }
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
