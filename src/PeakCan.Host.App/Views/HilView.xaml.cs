using System.Windows;
using System.Windows.Controls;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class HilView : UserControl
{
    public HilView()
    {
        InitializeComponent();
    }

    private TestCaseNode? FindCaseNode(StepNode stepNode) =>
        (DataContext as HilViewModel)?.ResultsTree.OfType<TestCaseNode>().FirstOrDefault(tc => tc.Steps.Contains(stepNode));

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


