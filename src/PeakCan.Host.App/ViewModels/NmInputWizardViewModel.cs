using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 补全 NM 输入的对话框 VM。建议值只填充编辑器，永不自动生效；
/// 只有「保存到项目」才会写 sidecar。
/// </summary>
public sealed partial class NmInputWizardViewModel : ObservableObject
{
    private readonly NmToolSnapshot _snapshot;
    private readonly NmMessageDef? _firstNm;
    private readonly ReplayFrame? _sampleFrame;
    // 编辑器预填的是 profile 当前值；记住初值才能区分「用户改过」与「只是回显」。
    private readonly int? _initialCycleMs;
    private readonly int? _initialTimeoutMs;

    [ObservableProperty] private byte? _nidByte;
    [ObservableProperty] private byte? _cbvByte;
    [ObservableProperty] private byte? _userDataOffset;
    [ObservableProperty] private int? _userDataLength;
    [ObservableProperty] private int? _cycleMs;
    [ObservableProperty] private int? _timeoutMs;
    [ObservableProperty] private bool? _pnEnabled;
    [ObservableProperty] private string _status = "";

    public NmInputWizardViewModel(NmToolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _firstNm = snapshot.Profile.Messages.FirstOrDefault(m => m.IsNm);
        _sampleFrame = _firstNm is null
            ? null
            : snapshot.Frames.FirstOrDefault(f =>
                (f.Id & 0x1FFFFFFFu) == ParseId(_firstNm.CanIdHex));

        CycleMs = snapshot.Profile.Timings.CycleMs;
        TimeoutMs = snapshot.Profile.Timings.TimeoutMs;
        _initialCycleMs = CycleMs;
        _initialTimeoutMs = TimeoutMs;

        SampleByteTexts = SampleBytes
            .Select(b => b.ToString("X2", CultureInfo.InvariantCulture))
            .ToArray();
        MissingItems = snapshot.Report.MissingInputs.ToArray();
    }

    public IReadOnlyList<NmInputMissing> MissingItems { get; }

    /// <summary>取当前 trace 里该 NM 报文的第一帧字节；没有样本时为空。</summary>
    public IReadOnlyList<byte> SampleBytes => _sampleFrame?.Data ?? Array.Empty<byte>();

    /// <summary>样本预览的十六进制文本（每字节两位大写 hex）。</summary>
    public IReadOnlyList<string> SampleByteTexts { get; }

    public int Dlc => _sampleFrame?.Dlc ?? _firstNm?.Dlc ?? 8;

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
        NidByte: NidByte,
        CbvByte: CbvByte,
        UserDataOffset: UserDataOffset,
        UserDataLength: UserDataLength,
        Timings: BuildTimings(),
        Features: PnEnabled is null ? null : new NmFeatures(PnEnabled: PnEnabled),
        DbcPath: _snapshot.DbcPath,
        DbcHash: _snapshot.DbcHash);

    /// <summary>
    /// 只回传用户真正改过的计时字段。编辑器预填的是 profile 当前值，
    /// 原样带回去等于凭空声明「用户覆盖了 DBC」，会污染 sidecar 语义。
    /// </summary>
    private NmTimings? BuildTimings()
    {
        var cycleChanged = CycleMs != _initialCycleMs;
        var timeoutChanged = TimeoutMs != _initialTimeoutMs;
        if (!cycleChanged && !timeoutChanged) return null;
        return new NmTimings(
            CycleMs: cycleChanged ? CycleMs : null,
            TimeoutMs: timeoutChanged ? TimeoutMs : null);
    }

    [RelayCommand]
    private void ApplySuggestion(NmInputMissing item)
    {
        // 只把建议值填进对应编辑器；不保存、不自动生效。
        Status = $"已填入建议：{item.Suggestion}（仍需点「保存到项目」）";
        if (item.InputKey == "cycle_ms" && _firstNm?.CycleMs is { } cycle) CycleMs = cycle;
        if (item.InputKey == "nid_byte") NidByte = 0;
        if (item.InputKey == "cbv_byte") CbvByte = 1;
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
            Status = "已保存到 " + NmSidecarStore.PathFor(_snapshot.DbcPath);
        }
        catch (Exception ex)
        {
            // 写 sidecar 失败（无权限/目录只读）必须回报给用户。
            Status = "保存失败：" + ex.Message;
        }
    }
}
