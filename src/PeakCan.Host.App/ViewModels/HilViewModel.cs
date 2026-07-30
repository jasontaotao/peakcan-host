using System.Collections.ObjectModel;
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

    [ObservableProperty] private string _dbcPath = "";
    [ObservableProperty] private string _suitePath = "";
    [ObservableProperty] private string _tracePath = "";
    [ObservableProperty] private bool _useHardware = false;
    [ObservableProperty] private string _hardwareChannel = "USB1";
    // Phase 3: ECU simulator + fault injection + matrix
    [ObservableProperty] private string _ecuScriptPath = "";
    [ObservableProperty] private string _matrixPath = "";
    [ObservableProperty] private bool _enableFaultInjection = false;
    [ObservableProperty] private bool _isRunning = false;
    [ObservableProperty] private double _progress = 0;
    [ObservableProperty] private string _statusMessage = "Ready";

    public ObservableCollection<TestCaseResultViewModel> Results { get; } = new();

    public HilViewModel(IHilRunnerService runner, ILogger<HilViewModel> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        StatusMessage = "Running...";
        Results.Clear();

        try
        {
            var request = new HilRunRequest(
                DbcPath, SuitePath,
                UseHardware ? null : TracePath,
                UseHardware ? HardwareChannel : null,
                EcuScriptPath: string.IsNullOrEmpty(EcuScriptPath) ? null : EcuScriptPath,
                MatrixPath: string.IsNullOrEmpty(MatrixPath) ? null : MatrixPath,
                EnableFaultInjection: EnableFaultInjection);

            var result = await _runner.RunAsync(request, null, default);

            foreach (var cr in result.CaseResults)
                Results.Add(new TestCaseResultViewModel(cr));

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

    private bool CanRun() => !IsRunning && !string.IsNullOrEmpty(SuitePath) && !string.IsNullOrEmpty(DbcPath)
        && (UseHardware || !string.IsNullOrEmpty(TracePath) || !string.IsNullOrEmpty(EcuScriptPath) || !string.IsNullOrEmpty(MatrixPath));
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
