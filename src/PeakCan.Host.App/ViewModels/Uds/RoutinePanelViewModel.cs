using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Panel VM for the Routines tab: DataGrid backed by RoutineDatabase.All,
/// Start/Stop/Query commands for the selected routine.
/// </summary>
public sealed partial class RoutinePanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private readonly RoutineDatabase _routineDb;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<RoutineRow> Routines { get; } = new();
    [ObservableProperty] private RoutineRow? _selectedRoutine;

    public RoutinePanelViewModel(UdsClient udsClient, RoutineDatabase routineDb)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(routineDb);
        _udsClient = udsClient;
        // v2.0.6 PATCH Bug-1: store the RoutineDatabase reference so
        // RefreshFromDatabase can re-populate after an ODX import.
        _routineDb = routineDb;

        foreach (var r in routineDb.All)
            Routines.Add(new RoutineRow { Id = r.Id, Name = r.Name });
        if (Routines.Count > 0) SelectedRoutine = Routines[0];
    }

    /// <summary>
    /// v2.0.6 PATCH Bug-1: re-populate the Routines DataGrid from
    /// <see cref="RoutineDatabase.All"/> after an ODX import. Mirrors
    /// <c>DtcPanelViewModel.RefreshFromDatabase</c>.
    /// </summary>
    public void RefreshFromDatabase()
    {
        Routines.Clear();
        foreach (var r in _routineDb.All)
        {
            Routines.Add(new RoutineRow { Id = r.Id, Name = r.Name });
        }
        if (Routines.Count > 0) SelectedRoutine = Routines[0];
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task StartRoutineAsync() => RunAsync(0x01);
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task StopRoutineAsync()  => RunAsync(0x02);
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task QueryRoutineAsync() => RunAsync(0x03);

    private bool CanRun() => SelectedRoutine is { Status: "Idle" or "Completed" or "Failed" or "Stopped" };

    private async Task RunAsync(byte subFunction)
    {
        var row = SelectedRoutine;
        if (row is null) return;
        var label = subFunction switch { 0x01 => "Start", 0x02 => "Stop", 0x03 => "Query", _ => $"subFn 0x{subFunction:X2}" };
        row.Status = "Running";
        NotifyCanExecuteChanged();
        try
        {
            AppendLog("Info", $"{label} routine 0x{row.Id:X4}...");
            // v2.0.6 PATCH Bug-3: no ConfigureAwait(false) — the catch block
            // sets row.Status and AppendLog writes to the shared
            // ObservableCollection; both need to stay on the UI dispatcher.
            var result = await _udsClient.RoutineControlAsync(subFunction, row.Id);
            row.LastResult = BitConverter.ToString(result).Replace("-", " ");
            row.Status     = "Completed";
            AppendLog("Info", $"{label} routine 0x{row.Id:X4} -> {row.LastResult}");
        }
        catch (UdsNegativeResponseException ex)
        {
            row.Status = "Failed";
            AppendLog("Warn", $"{label} routine 0x{row.Id:X4} failed: NRC 0x{(byte)ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            row.Status = "Failed";
            AppendLog("Error", $"{label} routine 0x{row.Id:X4} error: {ex.Message}");
        }
        finally
        {
            NotifyCanExecuteChanged();
        }
    }

    private void NotifyCanExecuteChanged()
    {
        StartRoutineCommand.NotifyCanExecuteChanged();
        StopRoutineCommand.NotifyCanExecuteChanged();
        QueryRoutineCommand.NotifyCanExecuteChanged();
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine($"{DateTime.Now:HH:mm:ss.fff}", level, message));

    partial void OnSelectedRoutineChanged(RoutineRow? value) => NotifyCanExecuteChanged();
}
