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
    private readonly Func<System.Collections.Generic.IReadOnlyList<string>?>? _measuredCommandsProvider;
    private readonly string _directory;

    /// <summary>两阶段确认（S5 评审 P2-2）：首击 Reconcile 出差异清单，再击确认执行写入。</summary>
    [ObservableProperty]
    private CalibrationReconcileReport? _pendingReport;

    /// <summary>可空注入构造（App VM 测试构造先例）。</summary>
    /// <param name="masterProvider">协议主站提供者（采集运行时返回 ActiveMaster）。</param>
    /// <param name="snapshotFactory">A2L 指纹来源（组合根接 ExportSnapshot，与 S4 记录同源）。</param>
    public XcpWritebackViewModel(
        XcpCardPanelViewModel? cards = null,
        Func<XcpMaster?>? masterProvider = null,
        Func<ContractSnapshot?>? snapshotFactory = null,
        Func<A2lDocument?>? documentProvider = null,
        Func<System.Collections.Generic.IReadOnlyList<string>?>? measuredCommandsProvider = null,
        string? directory = null)
    {
        _cards = cards;
        _masterProvider = masterProvider ?? (() => null);
        _snapshotFactory = snapshotFactory;
        _documentProvider = documentProvider ?? (() => null);
        _measuredCommandsProvider = measuredCommandsProvider;
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

    /// <summary>S8 变体区：基线参数集文件路径。</summary>
    [ObservableProperty]
    private string? _baselinePath;

    /// <summary>S8 变体区：delta 变体名（提取时写入文件 header）。</summary>
    [ObservableProperty]
    private string? _variantName;

    /// <summary>S8 变体区：delta 文件路径（提取输出 / 加载输入共用）。</summary>
    [ObservableProperty]
    private string? _deltaPath;

    /// <summary>S8 变体区：当前已加载变体 delta（非 null 时 Apply 走 delta 链路）。</summary>
    public bool IsDeltaLoaded => _loadedDelta is not null;

    private CalibrationVariantDelta? _loadedDelta;
    private CalibrationParameterSet? _baselineSet;

    /// <summary>D5 行内写值链路：卡片契约 → 段映射唯一入口 → writer（写 + 回读）。</summary>
    public async Task<CalibrationWriteOutcome> WriteSingleAsync(XcpCardViewModel card, double physicalValue)
    {
        ArgumentNullException.ThrowIfNull(card);
        var master = _masterProvider();
        if (master is null)
            return CalibrationWriteOutcome.WriteFailed("采集未运行（无协议连接），拒绝写值");
        if (IsBusy)
            return CalibrationWriteOutcome.WriteFailed("批量下发进行中，写值被拒绝（序列原子性保护）");
        if (!IsDownloadSupported())
            return CalibrationWriteOutcome.Rejected("能力对账拒绝：从机未实测 DOWNLOAD 支持（零线上流量）");

        if (card.Contract.DataType is null)
            return CalibrationWriteOutcome.Rejected($"对象 '{card.Name}' 元素数据类型未知");

        var loaded = LoadedDocument;
        if (loaded is null)
            return CalibrationWriteOutcome.WriteFailed("A2L 未加载，无法做段映射");

        // S6-T6：多段/多元素门禁解除——写入口经 CalibrationRunPlanner 切 run +
        // 元素广播（拒绝面在 writer 内，零线上流量）。
        await using var writer = new XcpCalibrationWriter(master);
        var outcome = await writer.WriteAsync(card.Contract, loaded, physicalValue).ConfigureAwait(true);
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
            var json = Encoding.UTF8.GetString(bytes);
            if (json.Contains("\"schemaVersion\": 2"))
            {
                // S8：变体 delta 文件——解析为 delta，包装 entries 走 S5 差异对账（D3-B）。
                var delta = _loadedDelta ?? CalibrationVariantDelta.Parse(json);
                delta.EnsureA2lMatches(sha);
                if (_baselineSet is not null)
                    delta.EnsureBaselineMatches(_baselineSet);
                set = CalibrationParameterSet.Export(
                    delta.Entries.ToList(), delta.A2lSha256, $"delta:{delta.VariantName}");
            }
            else
            {
                set = CalibrationParameterSet.Parse(json);
            }
            set.EnsureMatches(sha); // D2：指纹不符拒绝下发
        }
        catch (Exception ex)
        {
            StatusText = $"下发失败：{ex.Message}";
            return;
        }

        if (!IsDownloadSupported())
        {
            StatusText = "下发拒绝：能力对账未实测 DOWNLOAD 支持（零线上流量）。";
            return;
        }

        IsBusy = true;
        try
        {
            await using var writer = new XcpCalibrationWriter(master);
            var reconciler = new CalibrationReconciler(writer, master);

            // S5 评审 P2-2（D3 两阶段）：首击只对账出差异清单，再击确认才写入。
            if (PendingReport is null)
            {
                var preview = await reconciler.ReconcileAsync(set, new ContractSet(loaded), ct: default)
                    .ConfigureAwait(true);
                var diffs = preview.Entries.Count(e => e.Status == CalibrationEntryStatus.DiffFound);
                if (diffs == 0)
                {
                    StatusText = "对账完成：无差异项，无需下发。";
                    return;
                }
                PendingReport = preview;
                StatusText = $"对账发现 {diffs} 项差异。再次点击[下发参数集]确认写入（其余项将跳过）。";
                return;
            }

            var report = await reconciler.ApplyAsync(set, new ContractSet(loaded), onlyChanged: true)
                .ConfigureAwait(true);
            PendingReport = null;

            StatusText = $"下发完成：写 {report.WrittenCount}，跳过 {report.NoDifferenceCount + report.SkippedCount}，"
                + $"失败 {report.WriteFailedCount + report.ReadBackMismatchCount}（共 {report.Entries.Count} 项）"
                + (report.WriteFailedCount + report.ReadBackMismatchCount > 0 ? "——详见各项归因。" : "。");
        }
        catch (Exception ex)
        {
            // 批量编排异常（含 master 并发拒绝）转可见失败，不外逃（S5 评审 P1-2）。
            StatusText = $"下发失败：{ex.Message}";
            PendingReport = null;
        }
        finally
        {
            IsBusy = false;
        }
        ApplyParameterSetCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => !IsBusy && !string.IsNullOrWhiteSpace(ParameterSetPath);

    /// <summary>S8-T3 delta 提取（D3-B）：基线参数集 + 变体全量 → 差异项 delta 文件。</summary>
    [RelayCommand(CanExecute = nameof(CanExtractDelta))]
    private async Task ExtractDeltaAsync()
    {
        if (string.IsNullOrWhiteSpace(BaselinePath) || !File.Exists(BaselinePath))
        {
            StatusText = "delta 提取失败：基线参数集文件不存在。";
            return;
        }
        if (string.IsNullOrWhiteSpace(ParameterSetPath) || !File.Exists(ParameterSetPath))
        {
            StatusText = "delta 提取失败：变体参数集文件不存在。";
            return;
        }
        if (string.IsNullOrWhiteSpace(VariantName))
        {
            StatusText = "delta 提取失败：请输入变体名。";
            return;
        }
        if (string.IsNullOrWhiteSpace(DeltaPath))
        {
            StatusText = "delta 提取失败：请设置 delta 文件路径。";
            return;
        }

        try
        {
            var baselineBytes = await File.ReadAllBytesAsync(BaselinePath).ConfigureAwait(true);
            var baseline = CalibrationParameterSet.Parse(Encoding.UTF8.GetString(baselineBytes));
            var variantBytes = await File.ReadAllBytesAsync(ParameterSetPath).ConfigureAwait(true);
            var variant = CalibrationParameterSet.Parse(Encoding.UTF8.GetString(variantBytes));

            if (!string.Equals(baseline.A2lSha256, variant.A2lSha256, StringComparison.Ordinal))
            {
                StatusText = "delta 提取拒绝：基线与变体的 A2L 指纹不一致。";
                return;
            }

            var delta = CalibrationVariantDelta.Extract(VariantName!, baseline, variant, "extract");
            await File.WriteAllBytesAsync(DeltaPath, delta.ToJsonBytes()).ConfigureAwait(true);
            _baselineSet = baseline;
            StatusText = $"delta 提取完成：{VariantName}（{delta.Entries.Count} 项差异）→ {DeltaPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"delta 提取失败：{ex.Message}";
        }
        ExtractDeltaCommand.NotifyCanExecuteChanged();
    }

    private bool CanExtractDelta() => !IsBusy;

    /// <summary>S8-T3 delta 加载：解析 delta 文件 → 校验 A2L 指纹 → 存为待下发子集。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadDelta))]
    private async Task LoadDeltaAsync()
    {
        if (string.IsNullOrWhiteSpace(DeltaPath) || !File.Exists(DeltaPath))
        {
            StatusText = "delta 加载失败：delta 文件不存在。";
            return;
        }
        var sha = SnapshotSha();
        if (sha is null)
        {
            StatusText = "delta 加载失败：A2L 未加载（无指纹可比对）。";
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(DeltaPath).ConfigureAwait(true);
            var delta = CalibrationVariantDelta.Parse(Encoding.UTF8.GetString(bytes));
            delta.EnsureA2lMatches(sha);
            _loadedDelta = delta;
            StatusText = $"变体 delta 已加载：{delta.VariantName}（{delta.Entries.Count} 项子集）。使用 [下发参数集] 应用。";
        }
        catch (Exception ex)
        {
            _loadedDelta = null;
            StatusText = $"delta 加载失败：{ex.Message}";
        }
        LoadDeltaCommand.NotifyCanExecuteChanged();
    }

    private bool CanLoadDelta() => !IsBusy;

    partial void OnParameterSetPathChanged(string? value)
    {
        ExportParameterSetCommand.NotifyCanExecuteChanged();
        ApplyParameterSetCommand.NotifyCanExecuteChanged();
    }

    private A2lDocument? LoadedDocument => _documentProvider();

    /// <summary>能力对账门禁（S5 评审 P2-1）：实测命令面无 DOWNLOAD → 写路径零流量拒绝。</summary>
    private bool IsDownloadSupported() =>
        _measuredCommandsProvider?.Invoke() is not { } commands
        || commands.Any(c => c.Equals("DOWNLOAD", StringComparison.OrdinalIgnoreCase));

    private string? SnapshotSha() => _snapshotFactory?.Invoke()?.A2lSha256;
}
