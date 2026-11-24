using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.Uds.Rows;

/// <summary>One routine row for the Routines-tab DataGrid.</summary>
public sealed partial class RoutineRow : ObservableObject
{
    public ushort Id   { get; init; }
    public string Name { get; init; } = "";

    [ObservableProperty] private string  _status     = "Idle";
    [ObservableProperty] private string? _lastResult;

    /// <summary>
    /// 表格显示用中文状态。内部 <see cref="Status"/> 保持英文（CanRun 判据依赖它），
    /// 只有显示层经过 <see cref="UdsDisplayText.RoutineStatus"/> 翻译。
    /// </summary>
    public string StatusDisplay => UdsDisplayText.RoutineStatus(Status);

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusDisplay));
}
