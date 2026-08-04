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
    [ObservableProperty] private string _ecuScriptPath = "";
    [ObservableProperty] private string _matrixPath = "";
    [ObservableProperty] private bool _enableFaultInjection = false;
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

    /// <summary>PCAN 硬件通道下拉选项: USB1..USB16.</summary>
    public ObservableCollection<string> AvailableChannels { get; } = new()
    {
        "USB1", "USB2", "USB3", "USB4", "USB5", "USB6", "USB7", "USB8",
        "USB9", "USB10", "USB11", "USB12", "USB13", "USB14", "USB15", "USB16",
    };

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

    public HilViewModel(IHilRunnerService runner, ILogger<HilViewModel> logger, IFileDialogService fileDialog, IHilAnalysisService analysisService, IHilReportService reportService)
    {
        _runner = runner;
        _logger = logger;
        _fileDialog = fileDialog;
        _analysisService = analysisService;
        _reportService = reportService;
    }

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
        var path = _fileDialog.ShowOpenDialog("Test Suite JSON|*.json|All Files|*.*");
        if (path is not null)
        {
            SuitePath = path;
            LoadCaseList(path);
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
            if (doc.RootElement.TryGetProperty("cases", out var casesEl))
            {
                foreach (var caseEl in casesEl.EnumerateArray())
                {
                    var id = caseEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = caseEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    AvailableCases.Add(new TestCaseSelection { Id = id, Name = name });
                }
            }
        }
        catch
        {
            // 解析失败不阻塞 -- Run 时完整反序列化会报具体错误
        }
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
        var path = _fileDialog.ShowOpenDialog("ECU Script JSON|*.json|All Files|*.*");
        if (path is not null)
        {
            EcuScriptPath = path;
            EcuScriptPathSetExternally?.Invoke(path);
        }
    }

    [RelayCommand]
    private void BrowseMatrix()
    {
        var path = _fileDialog.ShowOpenDialog("Matrix Config JSON|*.json|All Files|*.*");
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
                    : null);

            var result = await _runner.RunAsync(request, progress, default);

            _lastResult = result;
            AnalyzeCommand.NotifyCanExecuteChanged();

            foreach (var cr in result.CaseResults)
                Results.Add(new TestCaseResultViewModel(cr));

            BuildResultsTree(result);

            StatusMessage = result.AllPassed
                ? $"All {result.TotalCases} cases passed"
                : $"{result.FailedCases}/{result.TotalCases} cases failed";

            // Phase 7 Unit C: 生成 HTML 报告。插入点在 StatusMessage 之后、Phase 7 A 的
            // AnalyzeAsync 之前 —— 报告是秒级本地 IO，不被 LLM 调用（最长 ~150s 超时）阻塞。
            // 失败不阻断测试结果展示（ShowReportError=true → UI 显示错误而非崩溃）。
            try
            {
                var report = _reportService.Generate(result);
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
