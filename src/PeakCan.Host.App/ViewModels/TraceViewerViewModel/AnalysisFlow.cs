using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // === Injected via ctor (T9 wires DI: 5 analysis params) ===
    // Fields are declared on the main partial (TraceViewerViewModel.cs) and
    // assigned by the ctor there; AnalysisFlow just consumes them.
    // v3.52.0 MINOR T9: removed the previous `= null!` placeholders that
    // existed before T9 — the ctor now wires real instances.

    /// <summary>Current in-memory analysis session, or null before a successful run.</summary>
    public AnalysisSession? CurrentAnalysisSession { get; private set; }

    /// <summary>P0 provider display name used to suppress the unavailable LLM section.</summary>
    public string LlmProviderDisplayName => _llmProvider.DisplayName;

    private bool CanRunAnalysis() => CurrentAnchorSnapshot is not null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRunAnalysis))]
    public async Task RunAnalysisAsync()
    {
        if (CurrentAnchorSnapshot is null)
        {
            ErrorMessage = "请先设绿/蓝锚并点『锁定 anchor 状态』";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "分析中…";

            double center = (CurrentAnchorSnapshot.GreenTimestampSeconds
                + CurrentAnchorSnapshot.BlueTimestampSeconds) / 2.0;
            var faultEvent = new FaultEvent(
                CenterTimestampSeconds: center,
                WindowBefore: TimeSpan.FromMilliseconds(500),
                WindowAfter: TimeSpan.FromMilliseconds(500),
                Description: $"auto-derived from anchors [{CurrentAnchorSnapshot.GreenTimestampSeconds:F3} .. {CurrentAnchorSnapshot.BlueTimestampSeconds:F3}]",
                CreatedAtUtc: DateTime.UtcNow);

            var evidence = _evidenceExtractor.Extract(
                faultEvent, CurrentAnchorSnapshot, _frameSource,
                dbc: null,
                dbcIdToSourceIdMap: new Dictionary<uint, string>());
            var report = _localAnalyzer.Analyze(evidence, faultEvent, CurrentAnchorSnapshot);

            CurrentAnalysisSession = _sessionRegistry.CreateOrUpdate(new AnalysisSession(
                SessionId: Guid.NewGuid(),
                Version: 0,
                FaultEvent: faultEvent,
                AnchorSnapshot: CurrentAnchorSnapshot,
                Report: report,
                CreatedAtUtc: DateTime.UtcNow));

            try
            {
                await _llmProvider.AnalyzeAsync(CurrentAnalysisSession, CancellationToken.None);
            }
            catch (NotImplementedException)
            {
                // P0 intentionally falls back to the deterministic local report.
            }

            OnPropertyChanged(nameof(CurrentAnalysisSession));
            StatusMessage = "分析完成";
        }
        catch (Exception ex)
        {
            LogAnalysisFailed(_logger, ex);
            ErrorMessage = $"分析失败: {ex.Message}";
            StatusMessage = "分析失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "AI analysis failed")]
    private static partial void LogAnalysisFailed(ILogger logger, Exception ex);
}
