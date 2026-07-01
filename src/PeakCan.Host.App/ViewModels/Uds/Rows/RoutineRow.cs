using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.Uds.Rows;

/// <summary>One routine row for the Routines-tab DataGrid.</summary>
public sealed partial class RoutineRow : ObservableObject
{
    public ushort Id   { get; init; }
    public string Name { get; init; } = "";

    [ObservableProperty] private string  _status     = "Idle";
    [ObservableProperty] private string? _lastResult;
}
