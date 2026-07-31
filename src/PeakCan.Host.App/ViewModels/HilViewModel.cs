using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilViewModel : ObservableObject
{
    private readonly IHilRunnerService _runner;
    private readonly ILogger<HilViewModel> _logger;
    private readonly IFileDialogService _fileDialog;

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

    // ECU editor: raw JSON content typed in the panel.
    [ObservableProperty] private string _ecuEditorJson = "";

    // Track the current ECU temp file so we can clean it up on next save.
    private string? _currentEcuTempPath;

    /// <summary>Flat result list for the summary DataGrid.</summary>
    public ObservableCollection<TestCaseResultViewModel> Results { get; } = new();

    /// <summary>Hierarchical result tree for the TreeView detail panel.</summary>
    public ObservableCollection<HilResultNode> ResultsTree { get; } = new();

    public HilViewModel(IHilRunnerService runner, ILogger<HilViewModel> logger, IFileDialogService fileDialog)
    {
        _runner = runner;
        _logger = logger;
        _fileDialog = fileDialog;
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
        if (path is not null) SuitePath = path;
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
        if (path is not null) EcuScriptPath = path;
    }

    [RelayCommand]
    private void BrowseMatrix()
    {
        var path = _fileDialog.ShowOpenDialog("Matrix Config JSON|*.json|All Files|*.*");
        if (path is not null) MatrixPath = path;
    }

    /// <summary>
    /// Save the ECU editor JSON to a temp file and set EcuScriptPath.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEcu))]
    private void SaveEcu()
    {
        if (string.IsNullOrWhiteSpace(EcuEditorJson)) return;
        // Clean up previous temp file before creating a new one
        if (_currentEcuTempPath is not null && File.Exists(_currentEcuTempPath))
        {
            try { File.Delete(_currentEcuTempPath); } catch { /* best-effort cleanup */ }
        }
        var tempPath = Path.Combine(Path.GetTempPath(), $"peakcan_ecu_{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, EcuEditorJson);
        _currentEcuTempPath = tempPath;
        EcuScriptPath = tempPath;
    }

    private bool CanSaveEcu() => !string.IsNullOrWhiteSpace(EcuEditorJson);

    // --- Analyze command (Sprint 14 stub) ---

    [RelayCommand]
    private Task AnalyzeAsync()
    {
        // Sprint 14: LLM-based failure analysis. Stub for now.
        _logger.LogInformation("AnalyzeCommand invoked (Sprint 14 stub)");
        return Task.CompletedTask;
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
                EnableAnalyze: false);

            var result = await _runner.RunAsync(request, progress, default);

            foreach (var cr in result.CaseResults)
                Results.Add(new TestCaseResultViewModel(cr));

            BuildResultsTree(result);

            StatusMessage = result.AllPassed
                ? $"All {result.TotalCases} cases passed"
                : $"{result.FailedCases}/{result.TotalCases} cases failed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HIL test execution failed");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
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
