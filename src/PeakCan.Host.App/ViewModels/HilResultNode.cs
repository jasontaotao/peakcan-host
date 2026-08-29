using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Abstract base for TreeView nodes in the HIL results panel.
/// </summary>
public abstract partial class HilResultNode : ObservableObject
{
    [ObservableProperty] private string _name = "";
}

/// <summary>
/// TreeView node representing a single test case result.
/// </summary>
public sealed partial class TestCaseNode : HilResultNode
{
    public ObservableCollection<StepNode> Steps { get; } = new();
}

/// <summary>
/// TreeView node representing a single test step result.
/// G6: Channel/ActualValue/ExpectedValue（仅非空时结果树展示，多通道失败归属）。
/// </summary>
public sealed partial class StepNode : HilResultNode
{
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _channel = "";
    [ObservableProperty] private string _actualValue = "";
    [ObservableProperty] private string _expectedValue = "";
    public ObservableCollection<FrameNode> Frames { get; } = new();
}

/// <summary>
/// TreeView node representing a captured CAN frame (shown on failure).
/// </summary>
public sealed partial class FrameNode : HilResultNode
{
    [ObservableProperty] private string _canId = "";
    [ObservableProperty] private string _dataHex = "";
}
