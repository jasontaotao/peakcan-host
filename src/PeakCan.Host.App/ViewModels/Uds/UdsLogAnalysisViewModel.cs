using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 日志分析页的 ViewModel。职责边界：文件选择、地址输入、触发分析、展示不可变报告、
/// 导出与 AI Handoff。所有分析逻辑在 Core；这里不解析报文、不碰硬件。
/// <para>
/// 重新分析开始时旧报告立即置空（D11）：分析失败时绝不能让上一份报告继续显示，
/// 否则用户会把两次分析的结果混在一起。
/// </para>
/// </summary>
public sealed partial class UdsLogAnalysisViewModel : ObservableObject
{
    private const string TraceFilter = "ASC/BLF (*.asc;*.blf)|*.asc;*.blf|所有文件 (*.*)|*.*";

    private readonly IUdsOfflineTraceLoader _loader;
    private readonly UdsOfflineAnalyzer _analyzer;
    private readonly ITraceViewerChatHandoffService _handoff;
    private readonly IFileDialogService _dialogs;
    private readonly IUdsExportPrompt _exportPrompt;
    private readonly DidDatabase _dids;
    private readonly RoutineDatabase _routines;
    private readonly OdxImportViewModel _odxImport;
    private CancellationTokenSource? _cts;
    // 运行代号：被新分析/关窗取代的旧任务不得再写 Report/Status/IsBusy。
    private int _analysisGeneration;

    public UdsLogAnalysisViewModel(
        IUdsOfflineTraceLoader loader,
        UdsOfflineAnalyzer analyzer,
        ITraceViewerChatHandoffService handoff,
        IFileDialogService dialogs,
        IUdsExportPrompt exportPrompt,
        DidDatabase dids,
        RoutineDatabase routines,
        OdxImportViewModel odxImport)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _exportPrompt = exportPrompt ?? throw new ArgumentNullException(nameof(exportPrompt));
        _dids = dids ?? throw new ArgumentNullException(nameof(dids));
        _routines = routines ?? throw new ArgumentNullException(nameof(routines));
        _odxImport = odxImport ?? throw new ArgumentNullException(nameof(odxImport));

        // 离线页看不到在线输出日志，ODX 导入结果必须在这里单独可见。
        _odxImport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OdxImportViewModel.LastStatus) or nameof(OdxImportViewModel.HasSuccessfulImport))
            {
                OnPropertyChanged(nameof(OdxStatusSummary));
                OnPropertyChanged(nameof(OdxReloadHint));
            }
        };
    }

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _requestIdText = "7E0";
    [ObservableProperty] private string _responseIdText = "7E8";
    [ObservableProperty] private string _channelText = "1";
    [ObservableProperty] private bool _isExtended;
    [ObservableProperty] private string _status = "未选择文件";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private UdsOfflineReport? _report;
    [ObservableProperty] private UdsOfflineTransaction? _selectedTransaction;
    [ObservableProperty] private string _filterText = "";

    public ObservableCollection<UdsOfflineTransaction> VisibleTransactions { get; } = new();
    public ObservableCollection<UdsFrameEvidence> SelectedEvidence { get; } = new();

    public UdsNoiseSummary? Noise => Report?.Noise;
    public bool HasReport => Report is not null;
    public bool IsOdxLoaded => Report?.Diagnostics.OdxLoaded ?? false;

    /// <summary>当前定义来源（最近一次 ODX 导入状态）；离线页必须显式可见。</summary>
    public string OdxStatusSummary =>
        _odxImport.LastStatus
        ?? (_odxImport.HasSuccessfulImport ? "ODX 已加载" : "未加载 ODX");

    /// <summary>导入新 ODX 后提示重新分析；旧报告不会热更新。</summary>
    public string? OdxReloadHint =>
        _odxImport.HasSuccessfulImport ? "已加载 ODX，重新分析以应用新定义（当前报告不变）" : null;

    /// <summary>有报告、但当前筛选无可见行时与“无匹配事务”区分开。</summary>
    public string? FilterEmptyHint =>
        HasReport && Report!.Transactions.Count > 0 && VisibleTransactions.Count == 0
            ? "当前筛选没有可见事务（清空筛选可恢复）"
            : null;

    public bool IsFilterEmpty => FilterEmptyHint is not null;

    /// <summary>选中事务的结论状态：把 NRC / 未闭合 / 归属歧义说清楚，不只靠 FinalKind 枚举名。</summary>
    public string? SelectedConclusion => SelectedTransaction switch
    {
        null => null,
        { HasDuplicateFinalResponse: true } => "归属歧义：收到重复最终响应，不判确定成功",
        { IsAmbiguous: true } => "归属歧义：同 SID 请求重叠，配对仅供参考",
        { FinalKind: UdsFinalKind.None } => "未闭合：日志结束前未收到通过校验的响应",
        { FinalKind: UdsFinalKind.Negative } t => $"最终 NRC 0x{t.FinalNrc:X2}",
        _ => null,
    };

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        var path = _dialogs.ShowOpenDialog(TraceFilter);
        if (path is null) return;
        FilePath = path;
        await AnalyzeAsync();
    }

    [RelayCommand]
    private Task Analyze() => AnalyzeAsync();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void InterpretSelected()
    {
        if (Report is null) return;
        _handoff.Handoff(UdsOfflineAiPromptBuilder.Build(Report, SelectedTransaction));
    }

    [RelayCommand]
    private Task Export() => ExportAsync();

    public async Task AnalyzeAsync(CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Status = "未选择文件";
            return;
        }

        _cts?.Cancel();
        var generation = ++_analysisGeneration;
        Report = null;
        VisibleTransactions.Clear();
        SelectedEvidence.Clear();
        IsBusy = true;
        IsPartial = false;
        Status = "分析中…";

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _cts = cts;
        try
        {
            var path = FilePath;
            var address = ParseAddress();
            Status = "读取文件中…";
            var frames = await _loader.LoadAsync(path, cts.Token).ConfigureAwait(true);
            if (generation != _analysisGeneration) return;
            Status = "分析中…";
            var request = new UdsOfflineAnalysisRequest(
                Guid.NewGuid(),
                Path.GetFileName(path),
                path,
                frames.Frames,
                address,
                SnapshotDiagnostics(),
                EngineVersion());

            // 不在 Task.Run 上挂 token：token 已取消时也要让分析器产出部分报告，
            // 而不是直接 TaskCanceledException 丢失已完成阶段。
            var report = await Task.Run(() => _analyzer.Analyze(request, cts.Token))
                .ConfigureAwait(true);

            // 已被更新的分析取代：这次结果作废，不得覆盖新报告/状态。
            if (generation != _analysisGeneration) return;

            Report = report;
            IsPartial = report.IsPartial;
            RefreshVisible();
            Status = report.IsPartial
                ? $"部分报告：{report.PartialReason}"
                : report.Transactions.Count == 0
                    ? (report.Counts.Input == 0
                        ? "日志为空：没有任何 CAN 帧"
                        : "当前设置下未识别到诊断事务")
                    : $"分析完成：{report.Transactions.Count} 个事务";
        }
        catch (OperationCanceledException)
        {
            if (generation == _analysisGeneration) Status = "已取消";
        }
        catch (UdsOfflineLoadException ex)
        {
            if (generation == _analysisGeneration) Status = "读取失败：" + ex.Message;
        }
        catch (FormatException)
        {
            if (generation == _analysisGeneration)
                Status = "地址格式错误：请用十六进制填写通道/请求 ID/响应 ID";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            if (generation == _analysisGeneration) IsBusy = false;
        }
    }

    public async Task ExportAsync()
    {
        if (Report is null)
        {
            Status = "没有可导出的报告";
            return;
        }

        var path = _exportPrompt.ChooseSavePath(out var isCsv);
        if (path is null) return;
        if (File.Exists(path) && !_exportPrompt.ConfirmOverwrite(path)) return;

        var choice = _exportPrompt.AskRedaction();
        if (choice == UdsExportChoice.Cancel) return;

        try
        {
            var redact = choice == UdsExportChoice.Redacted;
            var text = isCsv
                ? UdsOfflineReportWriter.WriteCsv(Report, redact)
                : UdsOfflineReportWriter.WriteMarkdown(Report, redact);
            await File.WriteAllTextAsync(path, text, Encoding.UTF8).ConfigureAwait(true);
            Status = (redact ? "已脱敏导出：" : "已导出：") + path;
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 关窗路径（UdsWindow.Unloaded 调用）：取消在途分析并丢弃报告。
    /// 报告只在窗口存续期间有效（spec §11.3）；重开窗口必须是新的空状态。
    /// 不弹任何对话框。
    /// </summary>
    public void StopForWindowClose()
    {
        _analysisGeneration++;   // 让在途任务的完成回调全部作废
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Report = null;
        VisibleTransactions.Clear();
        SelectedEvidence.Clear();
        SelectedTransaction = null;
        IsPartial = false;
        IsBusy = false;
        FilterText = "";
        Status = "未选择文件";
    }

    partial void OnReportChanged(UdsOfflineReport? value)
    {
        OnPropertyChanged(nameof(Noise));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(IsOdxLoaded));
        OnPropertyChanged(nameof(FilterEmptyHint));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    partial void OnFilterTextChanged(string value) => RefreshVisible();

    partial void OnSelectedTransactionChanged(UdsOfflineTransaction? value)
    {
        RefreshSelectedEvidence();
        OnPropertyChanged(nameof(SelectedConclusion));
    }

    private void RefreshSelectedEvidence()
    {
        SelectedEvidence.Clear();
        if (Report is null || SelectedTransaction is null) return;

        var indices = SelectedTransaction.Request.Evidence
            .Concat(SelectedTransaction.Responses.SelectMany(r => r.Evidence))
            .ToHashSet();
        foreach (var evidence in Report.Evidence.Where(e => indices.Contains(e.Index)))
            SelectedEvidence.Add(evidence);
    }

    private void RefreshVisible()
    {
        VisibleTransactions.Clear();
        if (Report is null) return;

        foreach (var transaction in Report.Transactions)
        {
            if (FilterText.Length == 0
                || (transaction.Interpretation?.ServiceDisplay.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false)
                || transaction.FinalKind.ToString().Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || (transaction.FinalNrc is { } nrc
                    && nrc.ToString("X2", CultureInfo.InvariantCulture).Contains(FilterText, StringComparison.OrdinalIgnoreCase)))
            {
                VisibleTransactions.Add(transaction);
            }
        }

        OnPropertyChanged(nameof(FilterEmptyHint));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    private UdsOfflineDiagnostics SnapshotDiagnostics() => new(
        _dids.All.ToArray(),
        _routines.All.ToArray(),
        _odxImport.HasSuccessfulImport,
        _dids.UserJsonLoadStatus,
        _routines.UserJsonLoadStatus,
        SnapshotId: $"{_dids.UserJsonLoadStatus}/{_routines.UserJsonLoadStatus}");

    private static string EngineVersion()
        => typeof(UdsLogAnalysisViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

    private UdsOfflineAddress ParseAddress()
    {
        static uint ParseHex(string text)
        {
            var cleaned = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!uint.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"不是合法的十六进制值：{text}");
            return value;
        }

        if (!ushort.TryParse(ChannelText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            throw new FormatException($"不是合法的通道号：{ChannelText}");

        return new UdsOfflineAddress(channel, ParseHex(RequestIdText), ParseHex(ResponseIdText), IsExtended);
    }
}
