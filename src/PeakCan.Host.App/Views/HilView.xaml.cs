using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the HIL tab. Hosts a WebView2 control that renders the
/// HIL HTML report (Phase 7 Unit C). WebView2 navigation is command-driven
/// (no DataBinding), so the view subscribes to the VM's <c>LatestReportPath</c>
/// change and calls <c>CoreWebView2.Navigate</c> with a file:// URI.
/// </summary>
public partial class HilView : UserControl
{
    private HilViewModel? _vm;
    // 防止 post-await 在视图卸载后写 VM（参考 ScriptView v1.2.13 PATCH Item 6）。
    private bool _isLoaded;
    // MEDIUM-2: WebView2 init 失败是视图本地状态 —— VM 的 ShowReportError 会被 Run 成功重置，
    // 但 _webView2Error 保留，fallback 持续显示（E1 兜底不失效）。
    private string? _webView2Error;

    public HilView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // B3：Unloaded 退订 PropertyChanged，防 tab 切换累积重复订阅（每次 Load 重新订阅）。
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as HilViewModel;
        if (_vm is null) return;
        _isLoaded = true;
        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateReportPanel();
        // G3（spec §4.2）: 窗口 Loaded 时刷新 Hardware 通道下拉（provider 拉模式无通知，
        // 只在 Loaded + Run 前拉两次；否则打开窗口下拉为空、多通道置灰态陈旧直到首次 Run）。
        _vm.RefreshAvailableChannels();

        try
        {
            await ReportWebView.EnsureCoreWebView2Async();
            if (!_isLoaded || _vm is null) return;
            // B2：导航到落盘文件（file:/// URI），绕开 WebView2 单字符串导航的 ~2MB 上限。
            // 报告已由 HilReportService 落盘，文件路径是唯一事实源。
            if (!string.IsNullOrEmpty(_vm.LatestReportPath))
                NavigateToReport(_vm.LatestReportPath);
        }
        catch (Exception ex)
        {
            // E1 + LOW-1：WebView2 Evergreen Runtime 缺失（如 Windows 10 未装）时降级 ——
            // VM 记录日志 + fallback 提示；"Open in Browser"（系统浏览器）仍可用。
            if (!_isLoaded) return;
            _webView2Error = $"WebView2 runtime 未安装或损坏: {ex.Message}. 请安装 WebView2 Evergreen Runtime.";
            _vm.OnReportWebView2InitFailed(ex, _webView2Error);
            UpdateReportPanel();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HilViewModel.LatestReportPath))
        {
            // LOW-2: 路径被清空（Run 开始/报告失败）时不导航；新报告生成时导航。
            if (!string.IsNullOrEmpty(_vm?.LatestReportPath))
                NavigateToReport(_vm.LatestReportPath);
        }
        // MEDIUM-1/2: 报告错误状态或 WebView 错误变化时重算 fallback 可见性。
        if (e.PropertyName is nameof(HilViewModel.ShowReportError) or nameof(HilViewModel.ReportError))
            UpdateReportPanel();
    }

    /// <summary>
    /// 统一管理 HTML Report tab 的 fallback/WebView 可见性（MEDIUM-1/2）：
    /// - 错误优先级：WebView2 init 失败（_webView2Error，视图本地）> VM 报告生成失败（ReportError）。
    /// - 任一错误存在 → fallback 可见；WebView2 是 HWND 控件（WPF airspace），错误时必须 Collapse 它，
    ///   否则 HWND 表面盖住 fallback 文字（MEDIUM-1）。
    /// </summary>
    private void UpdateReportPanel()
    {
        var error = _webView2Error ?? _vm?.ReportError;
        var hasError = !string.IsNullOrEmpty(error);

        ReportFallbackText.Text = error ?? "";
        ReportFallbackText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        // MEDIUM-1: 报告失败或 WebView2 不可用时 Collapse（移除 HWND，让 fallback 可见）。
        ReportWebView.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NavigateToReport(string filePath)
    {
        if (ReportWebView.CoreWebView2 is null) return;
        // file:///C:/... 形式（Path 转绝对 URI）。
        ReportWebView.CoreWebView2.Navigate(new Uri(filePath).AbsoluteUri);
    }
}
