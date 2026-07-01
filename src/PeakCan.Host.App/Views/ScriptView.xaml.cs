using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Script tab. Hosts a WebView2 control with
/// CodeMirror 6 for JavaScript editing.
/// </summary>
public partial class ScriptView : UserControl
{
    private ScriptViewModel? _viewModel;
    // v1.2.13 PATCH Item 6: guard against post-await side effects after the
    // view is unloaded. WPF may re-fire Loaded after Unloaded during
    // navigation; without this guard the post-await continuation writes
    // to a VM that may already have been replaced.
    private bool _isLoaded;

    public ScriptView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // v1.2.13 PATCH Item 6: also subscribe Unloaded so we can dispose
        // the WebView2 host. Without this the CoreWebView2 process leaks
        // across tab navigations (the previous view's process keeps running
        // until process exit).
        Unloaded += OnUnloaded;
    }

    // v1.2.13 PATCH Item 6: Unloaded must set _isLoaded = false so any
    // in-flight OnLoaded continuation short-circuits, and must Dispose
    // the EditorWebView so its CoreWebView2 native process is reaped.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        EditorWebView?.Dispose();
        EditorWebView = null!;
    }

    // v1.2.13 PATCH Item 6: test helpers — drive Loaded / Unloaded events
    // synchronously from unit tests without instantiating a full WPF
    // visual tree. Visible to App.Tests via InternalsVisibleTo.
    internal void RaiseLoadedForTesting() => OnLoaded(this, new RoutedEventArgs());
    internal void RaiseUnloadedForTesting() => OnUnloaded(this, new RoutedEventArgs());

    // v1.2.12 PATCH Item 7: wrap WebView2 init + NavigateToString in
    // try/catch. A missing or broken WebView2 Evergreen Runtime used to
    // throw into the async void sink and bubble up to
    // App.DispatcherUnhandledException (deterministic DoS of the Scripts
    // tab + process). We now surface the failure via IsEditorReady /
    // EditorError so the XAML fallback TextBlock can show a message
    // instead of crashing.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as ScriptViewModel;
        if (_viewModel is null) return;
        _isLoaded = true;

        try
        {
            await EditorWebView.EnsureCoreWebView2Async();

            // v1.2.13 PATCH Item 6: guard post-await VM writes.
            if (!_isLoaded) return;

            // Load CodeMirror editor from embedded HTML.
            var editorHtml = GetEditorHtml();
            EditorWebView.NavigateToString(editorHtml);

            if (!_isLoaded) return;
            _viewModel.IsEditorReady = true;
            _viewModel.EditorError = null;
        }
        catch (Exception ex)
        {
            // v1.2.13 PATCH Item 6: guard post-await VM writes.
            if (!_isLoaded) return;
            // v1.2.12 PATCH Item 7 review I-2/I-3: route through VM so the
            // exception is logged via [LoggerMessage] LogWebView2InitFailed
            // (EventId 4001). View stays free of logger plumbing.
            _viewModel.OnWebView2InitFailed(
                ex,
                $"WebView2 runtime 未安装或损坏: {ex.Message}. " +
                "请安装 WebView2 Evergreen Runtime.");
        }
    }

    private void EditorWebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;

        // Add JavaScript-to-C# bridge for script text synchronization.
        EditorWebView.CoreWebView2.AddHostObjectToScript("scriptBridge", new ScriptBridge(this));
    }

    /// <summary>
    /// Get the CodeMirror 6 editor HTML.
    /// </summary>
    private static string GetEditorHtml()
    {
        // Inline CodeMirror 6 with JavaScript language support.
        // In production, this would be loaded from an embedded resource.
        return """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <style>
                body {
                    margin: 0;
                    padding: 0;
                    overflow: hidden;
                    background: #1E1E1E;
                }
                #editor {
                    height: 100vh;
                    width: 100vw;
                }
            </style>
        </head>
        <body>
            <div id="editor"></div>
            <script type="module">
                // CodeMirror 6 inline bundle (simplified for MVP).
                // In production, use a bundled version from CDN or embedded resource.
                const editor = document.getElementById('editor');

                // Create a simple textarea as fallback.
                const textarea = document.createElement('textarea');
                textarea.style.cssText = `
                    width: 100%;
                    height: 100%;
                    background: #1E1E1E;
                    color: #D4D4D4;
                    font-family: 'Consolas', monospace;
                    font-size: 14px;
                    border: none;
                    outline: none;
                    resize: none;
                    padding: 8px;
                    box-sizing: border-box;
                `;
                textarea.placeholder = '// Write your JavaScript script here...\\n// Example: can.onFrame((frame) => { log(frame.id); });';
                editor.appendChild(textarea);

                // Expose getText/setText for C# bridge.
                window.getScriptText = () => textarea.value;
                window.setScriptText = (text) => { textarea.value = text; };

                // Ctrl+Enter to run.
                textarea.addEventListener('keydown', (e) => {
                    if (e.ctrlKey && e.key === 'Enter') {
                        // Notify C# to run the script.
                        window.chrome?.webview?.postMessage('run');
                    }
                });
            </script>
        </body>
        </html>
        """;
    }

    /// <summary>
    /// Bridge object exposed to JavaScript for script text synchronization.
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(true)]
    public class ScriptBridge
    {
        private readonly ScriptView _view;

        public ScriptBridge(ScriptView view)
        {
            _view = view;
        }

        /// <summary>
        /// Called from JavaScript when the script text changes.
        /// </summary>
        public void SetScriptText(string text)
        {
            if (_view._viewModel is not null)
            {
                _view._viewModel.ScriptText = text;
            }
        }

        /// <summary>
        /// Called from JavaScript to get the current script text.
        /// </summary>
        public string GetScriptText()
        {
            return _view._viewModel?.ScriptText ?? "";
        }
    }
}
