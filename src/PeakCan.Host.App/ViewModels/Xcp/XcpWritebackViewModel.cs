using System.IO;
using System.Text;
using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// S5-T4 标定写回面板（spec D5）：
/// <list type="bullet">
/// <item>行内写值：写回处理器经 <see cref="XcpCardPanelViewModel.AttachWriteback"/> 接到卡片，
/// 本类 <see cref="WriteSingleAsync"/> 承载"契约 → 段映射 → writer"链路。</item>
/// <item>参数集导出：关注集中 CHARACTERISTIC 卡片的当前数值 → 明文 JSON（D2 格式）。</item>
/// <item>参数集下发：加载 → 指纹校验（D2）→ 差异对账 → 只写差异项（D3）→ 结果单。</item>
/// </list>
/// master 经注入 provider（组合根接采集面板 ActiveMaster——写回共用采集连接）。
/// </summary>
public partial class XcpWritebackViewModel : ObservableObject
{
    private readonly XcpCardPanelViewModel? _cards;
    private readonly Func<ContractSnapshot?>? _snapshotFactory;
    private readonly Func<A2lDocument?> _documentProvider;
    private readonly Func<XcpMaster?> _masterProvider;
    private readonly string _directory;

    /// <summary>可空注入构造（App VM 测试构造先例）。</summary>
    /// <param name="masterProvider">协议主站提供者（采集运行时返回 ActiveMaster）。</param>
    /// <param name="snapshotFactory">A2L 指纹来源（组合根接 ExportSnapshot，与 S4 记录同源）。</param>
    public XcpWritebackViewModel(
        XcpCardPanelViewModel? cards = null,
        Func<XcpMaster?>? masterProvider = null,
        Func<ContractSnapshot?>? snapshotFactory = null,
        Func<A2lDocument?>? documentProvider = null,
        string? directory = null)
    {
        _cards = cards;
        _masterProvider = masterProvider ?? (() => null);
        _snapshotFactory = snapshotFactory;
        _documentProvider = documentProvider ?? (() => null);
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(AppContext.BaseDirectory, "recordings")
            : directory!;
    }

    /// <summary>参数集文件路径（导出/加载共用）。</summary>
    [ObservableProperty]
    private string? _parameterSetPath;

    /// <summary>写回状态行（导出/下发结果单摘要）。</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>批量下发进行中（按钮禁用）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>D5 行内写值链路：卡片契约 → 段映射唯一入口 → writer（写 + 回读）。</summary>
    public async Task<CalibrationWriteOutcome> WriteSingleAsync(XcpCardViewModel card, double physicalValue)
    {
        ArgumentNullException.ThrowIfNull(card);
        var master = _masterProvider();
        if (master is null)
            return CalibrationWriteOutcome.WriteFailed("采集未运行（无协议连接），拒绝写值");

        if (card.Contract.Segments.Count != 1)
            return CalibrationWriteOutcome.Rejected($"对象 '{card.Name}' 多段对象 v0.1 不支持");
        if (card.Contract.DataType is null)
            return CalibrationWriteOutcome.Rejected($"对象 '{card.Name}' 元素数据类型未知");

        var loaded = LoadedDocument;
        if (loaded is null)
            return CalibrationWriteOutcome.WriteFailed("A2L 未加载，无法做段映射");
        if (!XcpAddressMap.TryTranslate(loaded, card.Contract.Segments[0].Address, out var physical))
            return CalibrationWriteOutcome.Rejected($"对象 '{card.Name}' 段映射覆盖不到该地址（拒绝写）");

        await using var writer = new XcpCalibrationWriter(master);
        var outcome = await writer.WriteAsync(card.Contract, (uint)physical, physicalValue).ConfigureAwait(true);
        return outcome;
    }

    /// <summary>参数集导出（D2）：关注集 CHARACTERISTIC 卡片当前数值 → 明文 JSON。</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportParameterSetAsync()
    {
        if (_cards is null || string.IsNullOrWhiteSpace(ParameterSetPath))
            return;

        var entries = _cards.Cards
            .Where(c => string.Equals(c.Category, "CHARACTERISTIC", StringComparison.OrdinalIgnoreCase)
                        && c.LastNumericValue is not null)
            .Select(c => new CalibrationEntry(c.Name, c.LastNumericValue!.Value, c.Unit, null))
            .ToList();

        var sha = SnapshotSha();
        if (sha is null)
        {
            StatusText = "导出失败：A2L 未加载（无指纹可绑定）。";
            return;
        }
        if (entries.Count == 0)
        {
            StatusText = "导出失败：关注集无可导出的标定对象（需 CHARACTERISTIC 且已收到数据）。";
            return;
        }

        var set = CalibrationParameterSet.Export(entries, sha, "host-export");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ParameterSetPath)!);
            await File.WriteAllBytesAsync(ParameterSetPath, set.ToJsonBytes()).ConfigureAwait(true);
            StatusText = $"参数集已导出 {entries.Count} 项：{ParameterSetPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
            return;
        }
        ExportParameterSetCommand.NotifyCanExecuteChanged();
        ApplyParameterSetCommand.NotifyCanExecuteChanged();
    }

    private bool CanExport() =>
        !IsBusy
        && _cards is not null
        && _cards.Cards.Any(c => string.Equals(c.Category, "CHARACTERISTIC", StringComparison.OrdinalIgnoreCase)
                                 && c.LastNumericValue is not null)
        && !string.IsNullOrWhiteSpace(ParameterSetPath);

    /// <summary>参数集下发（D3）：加载 → 指纹校验 → 差异对账 → 只写差异项 → 结果单。</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyParameterSetAsync()
    {
        if (string.IsNullOrWhiteSpace(ParameterSetPath) || !File.Exists(ParameterSetPath))
        {
            StatusText = "下发失败：参数集文件不存在。";
            return;
        }
        var sha = SnapshotSha();
        if (sha is null)
        {
            StatusText = "下发失败：A2L 未加载（无指纹可比对）。";
            return;
        }
        var master = _masterProvider();
        if (master is null)
        {
            StatusText = "下发失败：采集未运行（无协议连接）。";
            return;
        }
        var loaded = LoadedDocument;
        if (loaded is null)
        {
            StatusText = "下发失败：A2L 未加载。";
            return;
        }

        CalibrationParameterSet set;
        try
        {
            var bytes = await File.ReadAllBytesAsync(ParameterSetPath).ConfigureAwait(true);
            set = CalibrationParameterSet.Parse(Encoding.UTF8.GetString(bytes));
            set.EnsureMatches(sha); // D2：指纹不符拒绝下发
        }
        catch (Exception ex)
        {
            StatusText = $"下发失败：{ex.Message}";
            return;
        }

        IsBusy = true;
        try
        {
            await using var writer = new XcpCalibrationWriter(master);
            var reconciler = new CalibrationReconciler(writer, master);
            var report = await reconciler.ApplyAsync(set, new ContractSet(loaded), onlyChanged: true)
                .ConfigureAwait(true);

            StatusText = $"下发完成：写 {report.WrittenCount}，跳过 {report.NoDifferenceCount + report.SkippedCount}，"
                + $"失败 {report.WriteFailedCount + report.ReadBackMismatchCount}（共 {report.Entries.Count} 项）"
                + (report.WriteFailedCount + report.ReadBackMismatchCount > 0 ? "——详见各项归因。" : "。");
        }
        finally
        {
            IsBusy = false;
        }
        ApplyParameterSetCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => !IsBusy && !string.IsNullOrWhiteSpace(ParameterSetPath);

    partial void OnParameterSetPathChanged(string? value)
    {
        ExportParameterSetCommand.NotifyCanExecuteChanged();
        ApplyParameterSetCommand.NotifyCanExecuteChanged();
    }

    private A2lDocument? LoadedDocument => _documentProvider();

    private string? SnapshotSha() => _snapshotFactory?.Invoke()?.A2lSha256;
}
