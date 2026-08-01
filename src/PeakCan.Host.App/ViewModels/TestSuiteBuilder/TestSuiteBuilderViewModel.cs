using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// Test Suite Builder 主 VM（HilStudioWindow col2）。构建用例/步骤, 参数从 DBC 独立下拉,
/// 保存走 TestSuite 强类型 + HILJsonOptions round-trip。
/// </summary>
public sealed partial class TestSuiteBuilderViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly IFileDialogService _fileDialog;
    private readonly ILogger _logger;
    private string? _suitePath;

    // suite-level pass-through 字段（round-trip 保真）
    public IReadOnlyList<string> GlobalCaseFixtureKeys { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> SuiteFixtureKeys { get; private set; } = Array.Empty<string>();

    public ObservableCollection<EditableTestCase> Cases { get; } = new();
    public IReadOnlyList<TestCaseStepKind> AvailableKinds => StepFieldDescriptors.AllKinds;
    public SendFrameComposerViewModel? Composer { get; }

    [ObservableProperty] private EditableTestCase? _selectedCase;
    [ObservableProperty] private EditableTestCaseStep? _selectedStep;
    [ObservableProperty] private string _status = "No suite loaded";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _suiteName = "";
    [ObservableProperty] private FailurePolicy _failurePolicy = FailurePolicy.ContinueAll;
    [ObservableProperty] private bool _continueAfterSetupFailure = true;
    [ObservableProperty] private int _timeoutMs;

    // DBC 独立下拉（DbcOptionsFlow）
    [ObservableProperty] private IReadOnlyList<DbcMessageOption> _dbcMessages = Array.Empty<DbcMessageOption>();
    [ObservableProperty] private IReadOnlyList<DbcSignalOption> _dbcSignals = Array.Empty<DbcSignalOption>();

    public TestSuiteBuilderViewModel(
        DbcService svc, ILogger logger, IFileDialogService? fileDialog = null, DbcEncodeService? encodeService = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        Composer = encodeService is null ? null : new SendFrameComposerViewModel(svc, encodeService, logger);
        _svc.DbcLoaded += (_) => ((Action)RefreshDbcOptions).RunOnUi();
        RefreshDbcOptions();
    }

    [RelayCommand]
    private void ComposeData()
    {
        if (SelectedStep is { Kind: TestCaseStepKind.SendFrame } && Composer is { } c)
        {
            var hex = c.ComposeHex();
            if (hex.Length > 0) SelectedStep.Params["Data"] = hex;
        }
    }

    [RelayCommand]
    private void AddStep(TestCaseStepKind kind)
    {
        if (SelectedCase is null) return;
        var step = EditableTestCaseStep.New(kind);
        SelectedCase.Steps.Add(step);
        SelectedStep = step;
    }

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedCase is null || SelectedStep is null) return;
        var idx = SelectedCase.Steps.IndexOf(SelectedStep);
        SelectedCase.Steps.RemoveAt(idx);
        SelectedStep = idx < SelectedCase.Steps.Count ? SelectedCase.Steps[idx] : SelectedCase.Steps.LastOrDefault();
    }

    [RelayCommand]
    private void MoveStepUp() => MoveStep(-1);

    [RelayCommand]
    private void MoveStepDown() => MoveStep(+1);

    private void MoveStep(int delta)
    {
        if (SelectedCase is null || SelectedStep is null) return;
        var idx = SelectedCase.Steps.IndexOf(SelectedStep);
        var target = idx + delta;
        if (target < 0 || target >= SelectedCase.Steps.Count) return;
        SelectedCase.Steps.Move(idx, target);
        SelectedStep = SelectedCase.Steps[target];
    }

    [RelayCommand]
    private void AddCase()
    {
        var c = new EditableTestCase { Id = $"case_{Cases.Count + 1}", Name = "New Case" };
        Cases.Add(c);
        SelectedCase = c;
    }

    [RelayCommand]
    private void RemoveCase()
    {
        if (SelectedCase is null) return;
        Cases.Remove(SelectedCase);
        SelectedCase = Cases.LastOrDefault();
    }

    public TestSuite ToSuite() => new(
        SuiteName ?? "Untitled",
        Cases.Select(c => c.ToCase()).ToList(),
        GlobalCaseFixtureKeys, SuiteFixtureKeys,
        new TestSuiteConfig(FailurePolicy, ContinueAfterSetupFailure),
        TimeoutMs);
}
