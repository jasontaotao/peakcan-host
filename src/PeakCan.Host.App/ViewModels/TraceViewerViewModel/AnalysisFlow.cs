using CommunityToolkit.Mvvm.ComponentModel;
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

    // W40 P2 PATCH: API Key setup UI state. Bound from the AI Analysis
    // panel (TraceViewerView.AIPanel.xaml). The DeepSeekProvider reads
    // the key via ICredentialStore directly — these properties expose
    // only metadata (configured/error/timestamp), never the key value.
    [ObservableProperty]
    private PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyStatus _apiKeyStatus =
        new(PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.NotSet);

    [ObservableProperty]
    private string _apiKeyStatusText = "未配置";

    [ObservableProperty]
    private string? _apiKeyLastError;

    [ObservableProperty]
    private bool _hasApiKeyError;

    [ObservableProperty]
    private bool _showApiKeySetup = true;

    // W40 P2 PATCH: refresh the status display after any UI operation.
    // Called by all 3 commands + the panel's Loaded event.
    private void UpdateApiKeyStatusDisplay(PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyStatus status)
    {
        ApiKeyStatus = status;
        ApiKeyStatusText = status.State switch
        {
            PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.Configured =>
                status.LastUpdatedAt is { } ts
                    ? $"已配置 (更新于 {ts:yyyy-MM-dd HH:mm})"
                    : "已配置",
            _ => "未配置",
        };
        ApiKeyLastError = status.LastError;
        HasApiKeyError = !string.IsNullOrEmpty(status.LastError);
    }

    [RelayCommand(CanExecute = nameof(CanSetApiKey))]
    private async Task SetApiKeyAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            UpdateApiKeyStatusDisplay(new(
                PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.NotSet,
                LastError: "API key is empty"));
            return;
        }
        UpdateApiKeyStatusDisplay(await _apiKeyManager.SetAsync(value));
    }

    private bool CanSetApiKey(string? value) => !string.IsNullOrWhiteSpace(value);

    [RelayCommand]
    private async Task RemoveApiKeyAsync()
    {
        UpdateApiKeyStatusDisplay(await _apiKeyManager.RemoveAsync());
    }

    // W40 P2 PATCH: "测试连接" — verify the current key is accepted by
    // DeepSeek (HTTP 200 vs 401). Stub provider call; does NOT trigger a
    // real network round-trip — the DeepSeekProvider returns Error envelope
    // without throwing on auth failure, so a missing/invalid key surfaces
    // via Result.ErrorCode rather than an exception.
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var status = await _apiKeyManager.CheckAsync();
        UpdateApiKeyStatusDisplay(status);
        // The actual HTTP probe is performed by RunAnalysisAsync when
        // the user later runs an analysis. TestConnection here is a
        // lightweight sanity check that the key is present + persisted;
        // we don't want to charge DeepSeek API tokens for a UI smoke
        // test. The StatusMessage updates to inform the operator.
        StatusMessage = status.State == PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.Configured
            ? "API key 已配置；运行分析时将自动调用 DeepSeek 验证"
            : "API key 未配置；运行分析会失败";
    }

    // === W41 MINOR: Streaming LLM Response (partial-summary UI) ===

    /// <summary>
    /// W41 MINOR: streaming summary bound to AI Analysis panel partial
    /// TextBlock. Appended incrementally as DeepSeekProvider emits
    /// LlmPartialUpdate.PartialSummary chunks.
    /// </summary>
    [ObservableProperty]
    private string _streamingSummary = "";

    /// <summary>
    /// W41 MINOR: streaming Evidence IDs accumulated as
    /// PartialEvidenceId chunks arrive; final filtered set lands in
    /// CurrentAnalysisSession.Report.AttributedEvidenceIds on FinalResult.
    /// </summary>
    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<string> _streamingEvidenceIds =
        System.Array.Empty<string>();

    [RelayCommand(CanExecute = nameof(CanRunAnalysis))]
    private async Task RunAnalysisStreamingAsync()
    {
        if (CurrentAnchorSnapshot is null)
        {
            ErrorMessage = "请先设绿/蓝锚并点『锁定 anchor 状态』";
            return;
        }

        IsLoading = true;
        StatusMessage = "分析中 (streaming)...";
        StreamingSummary = "";
        StreamingEvidenceIds = System.Array.Empty<string>();

        try
        {
            await foreach (var update in _llmProvider.AnalyzeStreamingAsync(CurrentAnalysisSession!, ct: default).ConfigureAwait(true))
            {
                switch (update)
                {
                    case LlmPartialUpdate.PartialSummary ps:
                        StreamingSummary += ps.Delta;
                        break;
                    case LlmPartialUpdate.PartialEvidenceId peid:
                        var newIds = StreamingEvidenceIds.Append(peid.EvidenceId).ToArray();
                        StreamingEvidenceIds = newIds;
                        break;
                    case LlmPartialUpdate.FinalResult fr:
                        // FinalResult carries LlmAnalysisResult (summary + cited IDs),
                        // not a full AnalysisSession — leave CurrentAnalysisSession
                        // from the local pass intact. StreamingSummary already
                        // contains the accumulated text from PartialSummary chunks.
                        StatusMessage = fr.Result.Error ?? "分析完成（流式）";
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"分析异常: {ex.GetType().Name}";
            _logger.LogWarning(ex, "RunAnalysisStreamingAsync failed");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
