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

    private bool CanRunAnalysis() => CurrentAnchorSnapshot is not null && !IsAnalyzing;

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
            IsAnalyzing = true;
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
                _analysisCts ??= new CancellationTokenSource();
                // v3.61.0 PATCH BUG-2: capture and merge LLM result into
                // CurrentAnalysisSession.Report.Summary so the UI actually
                // shows the LLM output. Previously discarded the return value.
                var llmResult = await _llmProvider.AnalyzeAsync(
                    CurrentAnalysisSession, _analysisCts.Token);
                if (llmResult.Error is not null)
                {
                    ErrorMessage = llmResult.Error;
                    StatusMessage = "LLM 分析异常";
                }
                else if (CurrentAnalysisSession is not null)
                {
                    // Merge LLM summary into the session report.
                    // The XAML binds to CurrentAnalysisSession.Report.Summary
                    // so this makes the LLM result user-visible.
                    CurrentAnalysisSession = CurrentAnalysisSession with
                    {
                        Report = CurrentAnalysisSession.Report with
                        {
                            Summary = llmResult.Summary,
                        },
                    };
                    OnPropertyChanged(nameof(CurrentAnalysisSession));
                }
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
            IsAnalyzing = false;
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

    // v3.61.0 MINOR: last operation result shown inside the AI panel
    // (not StatusMessage at window bottom — easier to see in context).
    // Operator-friendly text like "已保存" / "已清除" / "无效的 Key" etc.
    [ObservableProperty]
    private string? _panelOperationText;

    [ObservableProperty]
    private bool _hasPanelOperationError;

    // v3.61.0 PATCH: PasswordBox value buffer. Set by code-behind's
    // PasswordChanged handler (WPF PasswordBox.Password cannot be
    // reliably read via CommandParameter ElementName binding).
    internal string? PendingApiKeyValue { get; set; }

    /// <summary>
    /// v3.61.0 PATCH: probe credential store on startup so the API Key
    /// status reflects previously saved keys. Called fire-and-forget from
    /// the ctor. Safe because CheckAsync uses ConfigureAwait(true) which
    /// captures the ctor's SynchronizationContext (UI thread).
    /// </summary>
    internal async Task ProbeStoredApiKeyAsync()
    {
        try
        {
            var status = await _apiKeyManager.CheckAsync();
            UpdateApiKeyStatusDisplay(status);
        }
        catch
        {
            // Ignore — UI shows "未配置" as default fallback.
        }
    }

    // W40 P2 PATCH: refresh the status display after any UI operation.
    // Called by all 3 commands + the panel's Loaded event.
    private void UpdateApiKeyStatusDisplay(PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyStatus status)
    {
        ApiKeyStatus = status;
        ApiKeyStatusText = status.State switch
        {
            PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.Configured =>
                status.LastUpdatedAt is { } ts
                    ? $"已配置 (更新于 {ts.ToLocalTime():yyyy-MM-dd HH:mm})"
                    : "已配置",
            _ => "未配置",
        };
        ApiKeyLastError = status.LastError;
        HasApiKeyError = !string.IsNullOrEmpty(status.LastError);
    }

    [RelayCommand]
    private async Task SetApiKeyAsync()
    {
        var value = PendingApiKeyValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            HasPanelOperationError = true;
            PanelOperationText = "API Key 不能为空";
            UpdateApiKeyStatusDisplay(new(
                PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.NotSet,
                LastError: "API key is empty"));
            return;
        }
        var status = await _apiKeyManager.SetAsync(value);
        UpdateApiKeyStatusDisplay(status);
        HasPanelOperationError = status.State != PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.Configured;
        PanelOperationText = status.State == PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.Configured
            ? "已保存到 Windows Credential Manager"
            : $"保存失败：{status.LastError}";
    }

    [RelayCommand]
    private async Task RemoveApiKeyAsync()
    {
        var status = await _apiKeyManager.RemoveAsync();
        UpdateApiKeyStatusDisplay(status);
        HasPanelOperationError = false;
        PanelOperationText = "已清除";
    }

    // W40 P2 PATCH: "测试连接" — verify the current key is stored and
    // attempts a lightweight API probe to confirm it's accepted by
    // DeepSeek (HTTP 200 vs 401). The probe calls DeepSeek's models
    // list endpoint which does NOT consume tokens.
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        HasPanelOperationError = false;
        PanelOperationText = "检查中…";

        // Step 1: check local credential store
        var status = await _apiKeyManager.CheckAsync();
        UpdateApiKeyStatusDisplay(status);

        if (status.State != PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyConfiguredState.Configured)
        {
            HasPanelOperationError = true;
            PanelOperationText = "未检测到已保存的 API Key，请先录入并保存";
            StatusMessage = "API key 未配置；运行分析会失败";
            return;
        }

        // Step 2: probe DeepSeek API
        try
        {
            using var http = new System.Net.Http.HttpClient();
            // v3.61.0: lightweight models list probe — consumes zero tokens.
            var probeKey = await _apiKeyManager.ReadKeyRawAsync();
            if (probeKey is null)
            {
                HasPanelOperationError = true;
                PanelOperationText = "无法读取已保存的 API Key";
                StatusMessage = "API key 读取失败";
                return;
            }

            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", probeKey);
            var response = await http.GetAsync("https://api.deepseek.com/v1/models",
                CancellationToken.None);

            if (response.IsSuccessStatusCode)
            {
                HasPanelOperationError = false;
                PanelOperationText = "连接成功 ✓ DeepSeek API Key 有效";
                StatusMessage = "API key 有效";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HasPanelOperationError = true;
                PanelOperationText = "DeepSeek 返回 401 — API Key 无效";
                StatusMessage = "API key 无效（401）";
            }
            else
            {
                HasPanelOperationError = true;
                PanelOperationText = $"DeepSeek 返回 {(int)response.StatusCode} — 非预期响应";
                StatusMessage = $"API key 检测异常 (HTTP {(int)response.StatusCode})";
            }
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            HasPanelOperationError = true;
            PanelOperationText = $"网络错误：{ex.Message}";
            StatusMessage = "API key 检测失败（网络不可达）";
        }
        catch (TaskCanceledException)
        {
            HasPanelOperationError = true;
            PanelOperationText = "连接超时 — 请检查网络";
            StatusMessage = "API key 检测超时";
        }
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

        // v3.61.0 PATCH BUG-008: guard against null CurrentAnalysisSession.
        // RunAnalysisAsync sets it, but this streaming command can be invoked
        // independently (it calls AnalyzeStreamingAsync, not AnalyzeAsync).
        // If null, run the local pass first to establish the session.
        if (CurrentAnalysisSession is null)
        {
            StatusMessage = "请先运行一次完整分析（非流式）以建立分析会话";
            return;
        }

        IsAnalyzing = true;
        StatusMessage = "分析中 (streaming)...";
        StreamingSummary = "";
        StreamingEvidenceIds = System.Array.Empty<string>();

        try
        {
            _analysisCts ??= new CancellationTokenSource();
            await foreach (var update in _llmProvider.AnalyzeStreamingAsync(CurrentAnalysisSession, _analysisCts.Token).ConfigureAwait(true))
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
                        // v3.61.0 PATCH BUG-4: show errors in red ErrorMessage too,
                        // not just gray StatusMessage.
                        if (fr.Result.Error is not null)
                        {
                            ErrorMessage = fr.Result.Error;
                            StatusMessage = fr.Result.Error;
                        }
                        else
                        {
                            StatusMessage = "分析完成（流式）";
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // v3.61.0 PATCH BUG-009: set ErrorMessage so users see the
            // red bold text, not just gray StatusMessage.
            ErrorMessage = $"流式分析异常: {ex.Message}";
            StatusMessage = $"分析异常: {ex.GetType().Name}";
            _logger.LogWarning(ex, "RunAnalysisStreamingAsync failed");
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
