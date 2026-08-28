using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Reporting;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilViewModel : ObservableObject
{
    private readonly IHilRunnerService _runner;
    private readonly ILogger<HilViewModel> _logger;
    private readonly IFileDialogService _fileDialog;
    private readonly IHilAnalysisService _analysisService;
    private readonly IHilReportService _reportService;

    [ObservableProperty] private string _dbcPath = "";
    [ObservableProperty] private string _suitePath = "";
    [ObservableProperty] private string _tracePath = "";
    [ObservableProperty] private string _hardwareChannel = "USB1";

    /// <summary>G3: suite 声明多通道（declaredCount>1）→ Hardware 下拉置灰（通道由套件声明按序绑定）。</summary>
    [ObservableProperty] private bool _isMultiChannelSuite;
    [ObservableProperty] private string _ecuScriptPath = "";
    [ObservableProperty] private string _matrixPath = "";
    [ObservableProperty] private bool _enableFaultInjection = false;
    [ObservableProperty] private bool _captureCaseLogs = true; // 2026-08-15: 每 case 记录全量报文 (.asc)
    [ObservableProperty] private bool _isRunning = false;
    [ObservableProperty] private double _progressPercent = 0;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private HilMode _selectedMode = HilMode.TraceReplay;

    // LLM failure analysis (Sprint 16).
    [ObservableProperty] private string _analysisResult = "";
    [ObservableProperty] private bool _isAnalyzing = false;
    [ObservableProperty] private bool _enableAnalyze = false;
    private TestSuiteResult? _lastResult;

    // Web 报告 UI（Phase 7 Unit C）：最新报告文件路径 + 报告错误状态。
    [ObservableProperty] private string _latestReportPath = "";
    [ObservableProperty] private bool _showReportError = false;
    [ObservableProperty] private string _reportError = "";

    // ECU editor integration: see OpenEcuEditorRequested / EcuScriptPathSetExternally events below.

    /// <summary>Flat result list for the summary DataGrid.</summary>
    public ObservableCollection<TestCaseResultViewModel> Results { get; } = new();

    /// <summary>用例选择列表: Browse Suite 后填充, Run 时只运行选中的用例.</summary>
    public ObservableCollection<TestCaseSelection> AvailableCases { get; } = new();

    /// <summary>Hierarchical result tree for the TreeView detail panel.</summary>
    public ObservableCollection<HilResultNode> ResultsTree { get; } = new();

    /// <summary>PCAN 硬件通道下拉选项（G3）：动态刷新自已连接通道。Handle = "USB{n}" 值, Display = 显示文本。</summary>
    public ObservableCollection<HardwareChannelOption> AvailableChannels { get; } = new();

    // --- Mode-specific field visibility ---

    [ObservableProperty] private bool _isTraceMode = true;
    [ObservableProperty] private bool _isHardwareMode;
    [ObservableProperty] private bool _isVirtualEcuMode;
    [ObservableProperty] private bool _isMatrixMode;

    partial void OnSelectedModeChanged(HilMode value)
    {
        IsTraceMode = value == HilMode.TraceReplay;
        IsHardwareMode = value == HilMode.Hardware;
        IsVirtualEcuMode = value == HilMode.VirtualEcu;
        IsMatrixMode = value == HilMode.Matrix;
    }

    public HilViewModel(
        IHilRunnerService runner,
        ILogger<HilViewModel> logger,
        IFileDialogService fileDialog,
        IHilAnalysisService analysisService,
        IHilReportService reportService,
        // Spec v3 §3.4: 已连接通道提供者（AppShell 连接设置配的多路）。
        // 默认 null = 无已连通道 → 走单通道路径（零回归）。测试注入 fake。
        Func<IReadOnlyList<ConnectedChannel>>? connectedChannels = null)
    {
        _runner = runner;
        _logger = logger;
        _fileDialog = fileDialog;
        _analysisService = analysisService;
        _reportService = reportService;
        _connectedChannels = connectedChannels;
    }

    /// <summary>已连接通道的快照（host 打开时配好的多路：handle/波特率/FD）。</summary>
    public readonly record struct ConnectedChannel(ushort Handle, BaudRate BaudRate, bool Fd);

    /// <summary>硬件通道下拉项（G3）：Handle = 绑定值（"USB{n}"，下游 ParseChannelHandle 语义不变），Display = 显示连接信息。</summary>
    public sealed record HardwareChannelOption(string Handle, string Display);

    /// <summary>
    /// 多通道映射清单行（产品 review 补）：suite 声明的通道 → 绑定到的物理硬件口（按索引顺序）。
    /// 只读展示（维持 spec §4.2 "只展示不覆盖"裁决），消除"bus-a 到底对应哪块硬件"的黑盒。
    /// </summary>
    public sealed record ChannelBindingRow(string SuiteName, string Handle, string Detail);

    /// <summary>多通道映射清单（IsMultiChannelSuite 时展示；单通道空）。</summary>
    public ObservableCollection<ChannelBindingRow> ChannelBindings { get; } = new();

    private Func<IReadOnlyList<ConnectedChannel>>? _connectedChannels;

    /// <summary>多通道绑定截断提示（Run 完成后拼接到 StatusMessage，防被结果覆盖）。</summary>
    private string? _truncationWarning;

    /// <summary>
    /// Spec v3 §3.4: 注入已连接通道提供者（AppShell 构造时调用——DI factory 注入会
    /// 形成 AppShell⇄HilViewModel 循环死锁，用 setter 直连）。null 清除（单通道零回归）。
    /// </summary>
    public void SetConnectedChannelsProvider(Func<IReadOnlyList<ConnectedChannel>>? provider)
        => _connectedChannels = provider;

    // --- Browse commands ---

    [RelayCommand]
    private void BrowseDbc()
    {
        var path = _fileDialog.ShowOpenDialog("DBC Files|*.dbc|All Files|*.*");
        if (path is not null) DbcPath = path;
    }

    [RelayCommand]
    private void BrowseSuite()
    {
        var path = _fileDialog.ShowOpenDialog("Test Suite JSON|*.suite.json|All Files|*.*");
        if (path is not null)
        {
            SuitePath = path;
            LoadCaseList(path);
            // G3（spec §4.2）: 换套件后重算多通道置灰态 + 刷新下拉——否则从多通道切单通道
            // 下拉仍置灰（IsMultiChannelSuite 陈旧）直到下次 Run。
            RefreshAvailableChannels();
        }
    }

    /// <summary>轻量解析 Suite JSON, 只提取 cases[].id + cases[].name.</summary>
    private void LoadCaseList(string suitePath)
    {
        AvailableCases.Clear();
        try
        {
            var json = File.ReadAllText(suitePath);
            using var doc = JsonDocument.Parse(json);
            // G4 内容硬校验：顶层无 cases 数组 → 明确提示（防选错文件静默——原静默 catch 吞掉）
            if (!doc.RootElement.TryGetProperty("cases", out var casesEl))
            {
                StatusMessage = "不是测试套件文件（缺少 cases 字段）";
                return;
            }
            foreach (var caseEl in casesEl.EnumerateArray())
            {
                var id = caseEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = caseEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                AvailableCases.Add(new TestCaseSelection { Id = id, Name = name });
            }
        }
        catch (Exception ex)
        {
            // G4（spec §5.3）: 解析失败不静默——设明确提示（缺 cases 字段已在上文单独拦截，
            // 这里兜底 JSON 损坏/读取失败/字段类型异常；Run 时完整反序列化仍会报具体错误）。
            AvailableCases.Clear();
            StatusMessage = $"套件文件解析失败: {ex.Message}";
        }
    }

    /// <summary>
    /// Spec v3 §3.4: suite 声明的通道名按索引顺序绑定到 host 已连接通道。
    /// 返回 HardwareChannels（每项 ChannelConfig：Name=声明名、handle 空由 host
    /// 顺序映射、BaudRate/Fd 取已连通道实际值）；数量不一致按少的截断并提示。
    /// 无 suite.Channels、无提供者、或提供者空 → null（单通道零回归）。
    /// </summary>
    /// <summary>suite 通道声明的弱解析结果（G2：per-channel DBC/UDS 三字段透传）。</summary>
    private sealed record ChannelDeclaration(string Name, string? DbcPath, uint? UdsRequestId, uint? UdsResponseId);

    /// <summary>读 JSON 里的 UDS ID（uint?）。hil-core 存 JSON 数字；兼容 hex 字符串 "7E0"/"0x7E0"。</summary>
    private static uint? TryGetUdsId(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetUInt32();
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            return Convert.ToUInt32(hex, 16);
        }
        return null;
    }

    private IReadOnlyList<ChannelConfig>? BuildHardwareChannels()
    {
        if (string.IsNullOrEmpty(SuitePath) || _connectedChannels is null) return null;
        // Review MEDIUM-4: provider 只取一次快照（防动态提供者两次调用间变化导致索引越界）。
        var connected = _connectedChannels().ToList();
        if (connected.Count == 0) return null;

        // 读 suite.Channels 的声明（弱解析：失败时返回 null 走单通道）。
        // G2: 读 name/dbcPath/udsRequestId/udsResponseId 四字段（dbcPath/ID 缺省 null 合法）。
        // Review MEDIUM-1: 解析失败不静默——完整反序列化在 runner 兜底，记 warning 由调用方拼接。
        // Review MEDIUM-2: 空名元素计入声明数（参与截断判断），重名预检防 runner ToDictionary 抛。
        var declared = TryParseDeclaredChannels();
        if (declared is null)
        {
            _truncationWarning = "（无法读取 suite 通道声明——按单通道执行）";
            return null;
        }
        var declaredCount = declared.Count;
        if (declaredCount == 0) return null;

        // 重名声明预检：studio 编辑器允许重复名，runner 按名 ToDictionary 会抛。
        var dup = declared.GroupBy(d => d.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).FirstOrDefault();
        if (dup is not null)
        {
            _truncationWarning = $"（suite 通道名 '{dup}' 重复——请先在 studio 修正，本次按单通道执行）";
            return null;
        }

        var count = Math.Min(declaredCount, connected.Count);
        // G2: 状态栏提示各通道 DBC/UDS 绑定概况——明示 suite per-channel 配置覆盖界面全局 DBC（改配置回 studio）。
        var bindingSummary = string.Join("; ", Enumerable.Range(0, count)
            .Select(i => $"{declared[i].Name}:{FormatBindingDetail(declared[i])}"));
        _truncationWarning = (declaredCount != connected.Count
                ? $"（suite 声明 {declaredCount} 路，已连接 {connected.Count} 路，仅前 {count} 路参与执行）"
                : "")
            + $" 绑定[{bindingSummary}]（界面 DBC 已被 suite per-channel 覆盖，改配置回 studio）";

        var list = new List<ChannelConfig>(count);
        for (int i = 0; i < count; i++)
        {
            var c = connected[i];
            var d = declared[i];
            list.Add(new ChannelConfig(
                d.Name,
                "",                   // 空 handle → host 按索引顺序映射物理通道（spec v3 T13，厂商无关）
                c.BaudRate,           // 连接参数取 host 已连通道实际值
                c.Fd,
                d.DbcPath,            // G2: per-channel DBC 透传（不再丢弃）
                d.UdsRequestId,       // G2: per-channel UDS ID 透传（suite 配了 → host 建独立 UDS 栈）
                d.UdsResponseId));
        }
        return list;
    }

    /// <summary>弱解析 suite.Channels 声明（name/dbcPath/udsRequestId/udsResponseId）。无 channels 或解析失败 → null。</summary>
    private List<ChannelDeclaration>? TryParseDeclaredChannels()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath));
            if (doc.RootElement.TryGetProperty("channels", out var chEl))
            {
                return chEl.EnumerateArray()
                    .Select(e => new ChannelDeclaration(
                        e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        e.TryGetProperty("dbcPath", out var d) ? d.GetString() : null,
                        TryGetUdsId(e, "udsRequestId"),
                        TryGetUdsId(e, "udsResponseId")))
                    .ToList();
            }
        }
        catch
        {
            // 弱解析失败：BuildHardwareChannels 记 warning；RefreshAvailableChannels 当无声明
        }
        return null;
    }

    /// <summary>
    /// G3: 刷新硬件通道下拉（已连接通道）+ 多通道置灰标记 + 保留上次选择。
    /// 调用时机：HilWindow Loaded + 每次 Run 前（provider 是拉模式无通知，不做连接状态实时推送）。
    /// </summary>
    public void RefreshAvailableChannels()
    {
        // 解析 suite 通道声明数 → 多通道（declaredCount>1）置灰 Hardware 下拉（通道由套件声明按序绑定）
        var declared = string.IsNullOrEmpty(SuitePath) ? null : TryParseDeclaredChannels();
        IsMultiChannelSuite = (declared?.Count ?? 0) > 1;

        var previous = HardwareChannel;
        AvailableChannels.Clear();
        IReadOnlyList<ConnectedChannel> connected;
        try
        {
            // provider 是外部注入回调，异常不阻塞 UI 初始化/运行（否则 OnLoaded 的
            // async-void 裸调会变成未处理异常崩进程——review LOW 加固）。
            connected = _connectedChannels?.Invoke() ?? Array.Empty<ConnectedChannel>();
        }
        catch (Exception ex)
        {
            StatusMessage = $"获取已连接通道失败: {ex.Message}";
            connected = Array.Empty<ConnectedChannel>();
        }
        foreach (var c in connected)
        {
            var handle = $"USB{c.Handle - 0x50}";   // PCAN handle 0x51..0x60 → USB1..USB16
            AvailableChannels.Add(new HardwareChannelOption(handle, $"{handle}（已连接·{c.BaudRate.Name}）"));
        }
        // 产品 review: 多通道映射清单（suite 声明通道 → 物理口，按索引顺序）只读展示。
        RefreshChannelBindings(declared, connected);
        if (AvailableChannels.Count > 0)
        {
            // 保留上次选择（防连接顺序变化导致默认选中漂移）；记忆值不在当前列表才回退第一个
            if (!AvailableChannels.Any(o => o.Handle == previous))
                HardwareChannel = AvailableChannels[0].Handle;
        }
        else
        {
            // 无已连接通道：清空选择 → Hardware 模式 CanRun false + 状态提示
            HardwareChannel = "";
        }
    }

    /// <summary>
    /// 刷新多通道映射清单（产品 review 补，只读展示）：suite 声明的第 i 路通道按索引顺序
    /// 绑定到已连接的第 i 路物理口（与 BuildHardwareChannels 同一接线语义，见 spec v3 §3.4）。
    /// 数量不一致按少的截断（同 Run 截断语义，下方提示补齐）。
    /// </summary>
    private void RefreshChannelBindings(List<ChannelDeclaration>? declared, IReadOnlyList<ConnectedChannel> connected)
    {
        ChannelBindings.Clear();
        if (declared is null || declared.Count == 0) return;
        // 重名声明与 Run 路径（BuildHardwareChannels 返 null 走单通道）对齐：重名时映射无法按名区分，
        // 清空清单避免 UI 显示与 Run 实际行为不一致（studio 保存前已拦，此处为 host 兜底）。
        var dup = declared.GroupBy(d => d.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).FirstOrDefault();
        if (dup is not null) return;
        var count = Math.Min(declared.Count, connected.Count);
        for (int i = 0; i < count; i++)
        {
            var d = declared[i];
            var handle = $"USB{connected[i].Handle - 0x50}";
            ChannelBindings.Add(new ChannelBindingRow(d.Name, handle, FormatBindingDetail(d)));
        }
        if (declared.Count > connected.Count)
        {
            // 声明多于已连：未被绑定的声明通道也展示（标注未绑定，揭示"为什么少一路"）。
            for (int i = count; i < declared.Count; i++)
                ChannelBindings.Add(new ChannelBindingRow(declared[i].Name, "未绑定", "(已连接通道不足)"));
        }
    }

    /// <summary>per-channel DBC/UDS 绑定摘要（共享给映射清单 + Run 状态栏，防两处漂移）。</summary>
    private static string FormatBindingDetail(ChannelDeclaration d)
    {
        var dbc = d.DbcPath is { } dp ? $" {Path.GetFileName(dp)}" : " (全局DBC)";
        var uds = d.UdsRequestId is { } req ? $" UDS 0x{req:X3}/0x{d.UdsResponseId:X3}" : "";
        return $"{dbc}{uds}";
    }

    [RelayCommand]
    private void SelectAllCases()
    {
        foreach (var c in AvailableCases) c.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoCases()
    {
        foreach (var c in AvailableCases) c.IsSelected = false;
    }

    [RelayCommand]
    private void BrowseTrace()
    {
        var path = _fileDialog.ShowOpenDialog("Trace Files|*.asc;*.blf|All Files|*.*");
        if (path is not null) TracePath = path;
    }

    [RelayCommand]
    private void BrowseEcu()
    {
        var path = _fileDialog.ShowOpenDialog("ECU Script JSON|*.ecu.json|All Files|*.*");
        if (path is not null)
        {
            EcuScriptPath = path;
            EcuScriptPathSetExternally?.Invoke(path);
        }
    }

    [RelayCommand]
    private void BrowseMatrix()
    {
        var path = _fileDialog.ShowOpenDialog("Matrix Config JSON|*.matrix.json|All Files|*.*");
        if (path is not null) MatrixPath = path;
    }

    // --- ECU editor integration ---

    /// <summary>Raised when user clicks "Open ECU Editor" button in HIL view.</summary>
    public event Action? OpenEcuEditorRequested;

    /// <summary>Raised after BrowseEcu sets EcuScriptPath (path is non-null).</summary>
    public event Action<string>? EcuScriptPathSetExternally;

    [RelayCommand]
    private void OpenEcuEditor() => OpenEcuEditorRequested?.Invoke();

    // --- Analyze command (Sprint 16: LLM failure analysis) ---

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (_lastResult is null || _lastResult.AllPassed)
            return;

        IsAnalyzing = true;
        try
        {
            var result = await _analysisService.AnalyzeAsync(_lastResult);
            if (result is null)
            {
                AnalysisResult = "Analysis unavailable.";
            }
            else if (result.IsUnavailable)
            {
                AnalysisResult = result.UnavailableReason ?? "Analysis unavailable.";
            }
            else
            {
                AnalysisResult = result.Content;
            }
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout (150s in HilAnalysisService) surfaces as a
            // TaskCanceledException. Without this handler the command faults
            // silently (AsyncRelayCommand default) and the user gets no feedback
            // (code-review M2).
            AnalysisResult = "Analysis timed out.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HIL analysis failed");
            AnalysisResult = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            AnalyzeCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAnalyze()
        => !IsRunning && !IsAnalyzing && _lastResult is { AllPassed: false };

    // --- Open report command (Phase 7 Unit C) ---

    [RelayCommand]
    private void OpenReport()
    {
        if (string.IsNullOrEmpty(LatestReportPath) || !File.Exists(LatestReportPath)) return;
        try
        {
            // MEDIUM-3: File.Exists 与 Process.Start 间有竞态（文件被删），且 UseShellExecute 在
            // 无文件关联时抛 Win32Exception —— 包 try/catch，避免命令异常冒泡到 WPF Dispatcher。
            Process.Start(new ProcessStartInfo(LatestReportPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open HIL report {Path}", LatestReportPath);
            ReportError = $"Failed to open report: {ex.Message}";
            ShowReportError = true;
        }
    }

    /// <summary>
    /// Phase 7 Unit C (LOW-1): WebView2 Runtime 缺失等 init 失败时由 HilView 调用 ——
    /// 记录日志并设置报告错误状态（fallback 提示 + Open in Browser 兜底）。
    /// </summary>
    public void OnReportWebView2InitFailed(Exception ex, string message)
    {
        _logger.LogError(ex, "HIL report WebView2 init failed");
        ReportError = message;
        ShowReportError = true;
    }

    // --- Run command ---

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        StatusMessage = "Running...";
        Results.Clear();
        ResultsTree.Clear();
        ProgressPercent = 0;
        // LOW-2: 每次 Run 从空报告开始 —— 报告失败时不残留旧报告（WebView 不显示过期结果）。
        LatestReportPath = "";

        try
        {
            var progress = new Progress<TestProgress>(p => ProgressPercent = p.PercentComplete);
            _truncationWarning = null; // 每次 Run 重置（防上一次残留）

            // G3: Run 前刷新通道下拉（provider 是拉模式，Run 时取最新已连状态）
            RefreshAvailableChannels();

            // Spec v3 §3.4: 多通道执行接线——suite 声明的通道名按索引顺序绑定
            // 到 host 已连接通道（连接参数在 AppShell 打开时配好）。
            // 数量不一致按少的截断 + 状态栏提示。suite 无 Channels 或未连 → null（单通道零回归）。
            var hardwareChannels = BuildHardwareChannels();

            var request = new HilRunRequest(
                DbcPath, SuitePath,
                SelectedMode == HilMode.TraceReplay ? TracePath : null,
                SelectedMode == HilMode.Hardware ? HardwareChannel : null,
                EcuScriptPath: SelectedMode == HilMode.VirtualEcu ? (string.IsNullOrEmpty(EcuScriptPath) ? null : EcuScriptPath) : null,
                MatrixPath: SelectedMode == HilMode.Matrix ? (string.IsNullOrEmpty(MatrixPath) ? null : MatrixPath) : null,
                EnableFaultInjection: EnableFaultInjection,
                Mode: SelectedMode,
                EnableAnalyze: EnableAnalyze,
                SelectedCaseNames: AvailableCases.Count > 0
                    ? AvailableCases.Where(c => c.IsSelected).Select(c => c.Name).ToList()
                    : null,
                HardwareChannels: hardwareChannels,
                CaptureCaseLogs: CaptureCaseLogs);

            var result = await _runner.RunAsync(request, progress, default);

            _lastResult = result;
            AnalyzeCommand.NotifyCanExecuteChanged();

            foreach (var cr in result.CaseResults)
                Results.Add(new TestCaseResultViewModel(cr));

            BuildResultsTree(result);

            StatusMessage = result.AllPassed
                ? $"All {result.TotalCases} cases passed"
                : $"{result.FailedCases}/{result.TotalCases} cases failed";

            // 2026-08-15: 每 case 报文 log 成功时在状态栏提示实际写入目录（case-log P11）。
            if (CaptureCaseLogs && _runner.LastCaseLogDirectory is { } caseLogDir)
                StatusMessage += $" — case logs: {caseLogDir}";
            // Spec v3 §3.4: 多通道截断警告拼到结果后（防被覆盖）。
            if (_truncationWarning is not null)
                StatusMessage += $" {_truncationWarning}";

            // Phase 7 Unit C: 生成 HTML 报告。插入点在 StatusMessage 之后、Phase 7 A 的
            // AnalyzeAsync 之前 —— 报告是秒级本地 IO，不被 LLM 调用（最长 ~150s 超时）阻塞。
            // 失败不阻断测试结果展示（ShowReportError=true → UI 显示错误而非崩溃）。
            // DBC 数据源：取本次运行实际解析的文档（_runner.LastDbcDocument），避免 DbcService.Current
            // 指向 trace 面板的其它文件导致解码错 DBC；可能为 null（无 DBC），报告回落 hex 显示。
            try
            {
                // 多通道报告：取 per-channel DBC 字典（HeadlessHostBuilder 在 IAssertionContext
                // 工厂中按 ChannelId 注册）。单通道/无 DBC 时回落单 DBC 重载。
                var report = _runner.LastPerChannelDbcs is { Count: > 0 } dbcs
                    ? _reportService.Generate(result, dbcs, fallbackDbc: _runner.LastDbcDocument)
                    : _reportService.Generate(result, _runner.LastDbcDocument);
                LatestReportPath = report.FilePath;
                ShowReportError = false;
                ReportError = "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HIL report generation failed");
                ReportError = ex.Message;
                ShowReportError = true;
            }

            // Phase 7 Unit A: EnableAnalyze=true 且有失败 -> 自动分析（复用 AnalyzeAsync）。
            // 插入点在结果填充和 StatusMessage 之后，确保 UI 先渲染测试结果。
            // AnalyzeAsync 方法体不检查 IsRunning（此时仍为 true），仅依赖 _lastResult。
            if (EnableAnalyze && result.FailedCases > 0)
                await AnalyzeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HIL test execution failed");
            StatusMessage = $"Error: {ex.Message}";
            // Review MEDIUM-3: 异常路径也带截断/预检警告（防信息丢失）。
            if (_truncationWarning is not null)
                StatusMessage += $" {_truncationWarning}";
        }
        finally
        {
            IsRunning = false;
            AnalyzeCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRun()
    {
        if (IsRunning) return false;
        if (string.IsNullOrEmpty(SuitePath) || string.IsNullOrEmpty(DbcPath)) return false;

        return SelectedMode switch
        {
            HilMode.TraceReplay => !string.IsNullOrEmpty(TracePath),
            HilMode.Hardware => !string.IsNullOrEmpty(HardwareChannel),
            HilMode.VirtualEcu => !string.IsNullOrEmpty(EcuScriptPath),
            HilMode.Matrix => !string.IsNullOrEmpty(MatrixPath),
            _ => false,
        };
    }

    /// <summary>
    /// Build the hierarchical result tree from a TestSuite result.
    /// Frame nodes are only added for steps that captured frames around a failure.
    /// </summary>
    private void BuildResultsTree(TestSuiteResult result)
    {
        ResultsTree.Clear();
        foreach (var cr in result.CaseResults)
        {
            var caseNode = new TestCaseNode { Name = cr.TestCaseName };
            foreach (var step in cr.StepResults)
            {
                var stepNode = new StepNode
                {
                    Name = step.Label ?? $"Step {step.StepIndex}",
                    Status = step.Status.ToString(),
                    Message = step.Message ?? "",
                    // G6: 结果树展示通道归属 + Actual/Expected（仅非空时 XAML 渲染）
                    Channel = step.Channel ?? "",
                    ActualValue = step.ActualValue ?? "",
                    ExpectedValue = step.ExpectedValue ?? "",
                };
                if (step.FramesAroundFailure is { Count: > 0 })
                {
                    foreach (var f in step.FramesAroundFailure)
                    {
                        stepNode.Frames.Add(new FrameNode
                        {
                            Name = $"0x{f.Id.Raw:X3}",
                            CanId = $"0x{f.Id.Raw:X3}",
                            DataHex = BitConverter.ToString(f.Data.ToArray()).Replace("-", " "),
                        });
                    }
                }
                caseNode.Steps.Add(stepNode);
            }
            ResultsTree.Add(caseNode);
        }
    }
}

public sealed partial class TestCaseResultViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _passed;
    [ObservableProperty] private string _failureReason = "";

    public TestCaseResultViewModel() { }

    public TestCaseResultViewModel(TestCaseResult result)
    {
        _name = result.TestCaseName;
        _passed = result.Passed;
        _failureReason = result.FailureReason ?? "";
    }
}

/// <summary>用例选择项: 显示在 Test Cases tab, 用户勾选要运行的用例.</summary>
public sealed partial class TestCaseSelection : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    public required string Id { get; init; }
    public required string Name { get; init; }
}
