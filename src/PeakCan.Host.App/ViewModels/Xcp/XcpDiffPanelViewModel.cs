using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Diff;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// S6-T5 参数集 diff 面板 VM（spec D5）。只读可视化：两份 S5 参数集文件 →
/// Core diff 行集 → 表格展示（越限标红）；选中行定位 XcpView 对象卡片（事件由视图承接）。
/// 应用走 S5 既有 apply 链路，本面板零写入口。文件加载/指纹基准/限值来源由组合根注入
///（App 不碰协议原语；限值经 ContractSet 解析期字段，不现算）。
/// </summary>
public sealed partial class XcpDiffPanelViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<CalibrationParameterSet>> _loadSet;
    private readonly Func<string?>? _currentA2lSha256Provider;
    private readonly Func<CalibrationLimitsLookup?>? _limitsLookupProvider;

    public XcpDiffPanelViewModel(
        Func<string, CancellationToken, Task<CalibrationParameterSet>>? loadSet = null,
        Func<string?>? currentA2lSha256Provider = null,
        Func<CalibrationLimitsLookup?>? limitsLookupProvider = null)
    {
        _loadSet = loadSet ?? ((_, _) =>
            Task.FromException<CalibrationParameterSet>(
                new InvalidOperationException("组合根未接线参数集加载函数")));
        _currentA2lSha256Provider = currentA2lSha256Provider;
        _limitsLookupProvider = limitsLookupProvider;
    }

    /// <summary>diff 行集（Ordinal 排序；未变化对象不出现——Core Compute 保证）。</summary>
    public ObservableCollection<CalibrationDiffRow> Rows { get; } = new();

    [ObservableProperty]
    private string? _baselinePath;

    [ObservableProperty]
    private string? _targetPath;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private CalibrationDiffRow? _selectedRow;

    /// <summary>定位卡片请求（视图订阅：滚动 XcpView 卡片格到该对象）。</summary>
    public event Action<string>? CardLocateRequested;

    [RelayCommand]
    public async Task LoadAndDiffAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BaselinePath) || string.IsNullOrWhiteSpace(TargetPath))
        {
            StatusText = "请先填写基线与目标参数集文件路径";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var baseline = await _loadSet(BaselinePath, ct).ConfigureAwait(true);
            var target = await _loadSet(TargetPath, ct).ConfigureAwait(true);

            Rows.Clear();
            foreach (var row in CalibrationParameterSetDiff.Compute(baseline, target, _limitsLookupProvider?.Invoke()))
                Rows.Add(row);

            var changed = Rows.Count(r => r.Kind == ChangeKind.Changed);
            var added = Rows.Count(r => r.Kind == ChangeKind.Added);
            var removed = Rows.Count(r => r.Kind == ChangeKind.Removed);
            StatusText = $"diff 完成：{Rows.Count} 项差异（改动 {changed} / 新增 {added} / 删除 {removed}）";

            // 指纹口径与回放页同源（S4 快照）：与当前 A2L 不符只警示不拒绝——
            // diff 是只读比较面，跨固件重看是 S1 §5.3-5 明文场景；下发门禁在 S5 apply。
            var currentSha = _currentA2lSha256Provider?.Invoke();
            if (currentSha is not null
                && (!string.Equals(baseline.A2lSha256, currentSha, StringComparison.Ordinal)
                    || !string.Equals(target.A2lSha256, currentSha, StringComparison.Ordinal)))
            {
                StatusText += "｜警示：参数集指纹与当前 A2L 不符（仅可看，下发会被 S5 拒绝）";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Rows.Clear();
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Locate(CalibrationDiffRow? row)
    {
        if (row is null)
            return;
        CardLocateRequested?.Invoke(row.Name);
    }
}
