using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Script tab. Hosts a WebView2 control that renders the
/// editor page produced by <see cref="ScriptEditorHtml"/>; editor text flows
/// back through <see cref="ScriptEditorBridge"/> (postMessage JSON).
/// </summary>
public partial class ScriptView : UserControl
{
    private ScriptViewModel? _viewModel;
    // 页面 → VM 的解析层；第一次收到消息时创建（_viewModel 在 OnLoaded 才赋值）。
    private ScriptEditorBridge? _bridge;
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

    // v2.0.7 PATCH Bug-5: previously this method disposed the
    // EditorWebView and nulled its C# field on every Unloaded. The
    // UserControl is unloaded every time the user switches to another
    // tab and re-loaded when they come back. Disposing the WebView2
    // nuked its CoreWebView2 process, but the WebView2 loader's
    // process-wide CoreWebView2Environment handle stayed cached —
    // re-creating a WebView2 against that cached env failed with
    // "WebView2 runtime not installed or corrupted" on every second
    // tab entry. The v1.2.13 PATCH Item 6 Disposed-too-eagerly
    // pattern is correct for full UserControl destruction (process
    // exit) but wrong for WPF tab navigation.
    //
    // Fix: only set _isLoaded = false on tab-switch Unload. Do NOT
    // dispose the WebView2 — it stays alive for the process lifetime
    // (only one ScriptView exists, so this is one CoreWebView2
    // process, not a leak). The WebView2's finalizer runs when the
    // AppShell window closes; for an extra-clean shutdown we also
    // dispose on the app's Exit event (see App.xaml.cs).
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
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
        // DataContext 可能被换掉（视图实例被复用），桥必须跟着换，不能缓存旧的 VM。
        _bridge = null;

        try
        {
            await EditorWebView.EnsureCoreWebView2Async();

            // v1.2.13 PATCH Item 6: guard post-await VM writes.
            if (!_isLoaded) return;

            // 编辑器页面 + 回填草稿。初值直接嵌在 HTML 里而不是导航后再灌：
            // 每次切回本 tab 都会重新 NavigateToString，嵌入初值才能保住草稿。
            EditorWebView.NavigateToString(ScriptEditorHtml.Build(_viewModel.ScriptText));

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

        // 页面 → C# 的唯一通道：postMessage(JSON)。解析交给 ScriptEditorBridge，
        // 这里只负责"要不要运行"这一步（见 ScriptEditorHtml 顶部的协议说明）。
        // WebMessageReceived 在 UI 线程触发，因此直接写 VM 是安全的。
        // 先退订再订阅：WebView2 若因崩溃/重建再次触发本事件，重复订阅会让
        // 同一条 run 消息执行两次。
        EditorWebView.CoreWebView2.WebMessageReceived -= OnEditorWebMessageReceived;
        EditorWebView.CoreWebView2.WebMessageReceived += OnEditorWebMessageReceived;
    }

    private void OnEditorWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_viewModel is null) return;

        string? json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            // 非字符串载荷不属于编辑器的协议，忽略即可。
            return;
        }

        var bridge = _bridge ??= new ScriptEditorBridge(_viewModel);
        if (bridge.HandleMessage(json) && _viewModel.RunCommand.CanExecute(null))
        {
            _viewModel.RunCommand.Execute(null);
        }
    }
}
