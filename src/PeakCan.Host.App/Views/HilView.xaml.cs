using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the HIL tab. Hosts a WebView2 control that renders the
/// HIL HTML report and copies structured failure details on request.
/// </summary>
public partial class HilView : UserControl
{
    private HilViewModel? _vm;
    private bool _isLoaded;
    private string? _webView2Error;

    public HilView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as HilViewModel;
        if (_vm is null) return;
        _isLoaded = true;
        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateReportPanel();
        _vm.RefreshAvailableChannels();

        try
        {
            await ReportWebView.EnsureCoreWebView2Async();
            if (!_isLoaded || _vm is null) return;
            if (!string.IsNullOrEmpty(_vm.LatestReportPath))
                NavigateToReport(_vm.LatestReportPath);
        }
        catch (Exception ex)
        {
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
        if (e.PropertyName == nameof(HilViewModel.LatestReportPath) &&
            !string.IsNullOrEmpty(_vm?.LatestReportPath))
            NavigateToReport(_vm.LatestReportPath);

        if (e.PropertyName is nameof(HilViewModel.ShowReportError) or nameof(HilViewModel.ReportError))
            UpdateReportPanel();
    }

    private void UpdateReportPanel()
    {
        var error = _webView2Error ?? _vm?.ReportError;
        var hasError = !string.IsNullOrEmpty(error);

        ReportFallbackText.Text = error ?? "";
        ReportFallbackText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        ReportWebView.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NavigateToReport(string filePath)
    {
        if (ReportWebView.CoreWebView2 is null) return;
        ReportWebView.CoreWebView2.Navigate(new Uri(filePath).AbsoluteUri);
    }

    private TestCaseNode? FindCaseNode(StepNode stepNode) =>
        (DataContext as HilViewModel)?.ResultsTree.OfType<TestCaseNode>()
            .FirstOrDefault(tc => tc.Steps.Contains(stepNode));

    private void StepContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepNode stepNode } element &&
            element.ContextMenu is { } menu)
        {
            menu.Visibility = stepNode.Status == "Failed"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void CopyFailureDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StepNode stepNode } ||
            FindCaseNode(stepNode) is not { } caseNode ||
            DataContext is not HilViewModel vm) return;
        Clipboard.SetText(HilViewFailureCopy.BuildFailureDetail(vm.LastSuiteName, caseNode, stepNode));
    }
}

internal static class HilViewFailureCopy
{
    internal static string BuildFailureDetail(string suiteName, TestCaseNode caseNode, StepNode stepNode) =>
        string.Join(System.Environment.NewLine,
            $"Suite: {suiteName}",
            $"Case: {caseNode.Name}",
            $"Step: {stepNode.Name}",
            $"Status: {stepNode.Status}",
            $"Message: {stepNode.Message}",
            $"Actual: {stepNode.ActualValue}",
            $"Expected: {stepNode.ExpectedValue}",
            $"Channel: {stepNode.Channel}",
            $"Frames: {string.Join("; ", stepNode.Frames.Select(f => $"{f.CanId} {f.DataHex}"))}");
}
