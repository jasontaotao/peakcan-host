using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: Logging helpers (v1.2.12 PATCH Item 7 + earlier).
    // Methods moved verbatim from ScriptViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - RunAsync -> LogScriptCompleted / LogScriptFailed / LogScriptException
    //   - Stop -> LogScriptStopped
    //   - OnWebView2InitFailed (public API for ScriptView.OnLoaded WebView2 init failures)

    [LoggerMessage(Level = LogLevel.Information, Message = "Script completed successfully")]
    private static partial void LogScriptCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script failed: {Error}")]
    private static partial void LogScriptFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Script execution exception")]
    private static partial void LogScriptException(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Script stopped by user")]
    private static partial void LogScriptStopped(ILogger logger);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Error, Message = "WebView2 init failed")]
    private static partial void LogWebView2InitFailed(ILogger logger, Exception ex);

    /// <summary>
    /// v1.2.12 PATCH Item 7 review I-2/I-3: invoked by ScriptView.OnLoaded
    /// when EnsureCoreWebView2Async or NavigateToString throws. Sets
    /// IsEditorReady = false + EditorError message AND logs the underlying
    /// exception via [LoggerMessage] LogWebView2InitFailed. Keeps the WPF
    /// view free of logger plumbing (DI-incompatible) while satisfying
    /// the spec's logging requirement.
    /// </summary>
    public void OnWebView2InitFailed(Exception ex, string message)
    {
        LogWebView2InitFailed(_logger, ex);
        IsEditorReady = false;
        EditorError = message;
    }
}
