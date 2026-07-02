using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Orchestrator for the UDS diagnostic tab. Holds four panel ViewModels
/// (Session / DID / Routine / DTC), a shared output log, and a clear-output
/// command. Owns no UdsClient interaction of its own — each panel owns its
/// own RelayCommands and talks to UdsClient directly.
/// </summary>
public sealed partial class UdsViewModel : ObservableObject
{
    public SessionPanelViewModel Session { get; }
    public DidPanelViewModel     Did     { get; }
    public RoutinePanelViewModel Routine { get; }
    public DtcPanelViewModel     Dtc     { get; }
    public OdxImportViewModel    OdxImport { get; }

    /// <summary>Shared UDS log; all panels append here.</summary>
    public ObservableCollection<UdsLogLine> OutputLog { get; } = new();

    public UdsViewModel(
        SessionPanelViewModel session,
        DidPanelViewModel     did,
        RoutinePanelViewModel routine,
        DtcPanelViewModel     dtc,
        OdxImportViewModel    odxImport)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(did);
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(dtc);
        ArgumentNullException.ThrowIfNull(odxImport);

        Session = session;
        Did     = did;
        Routine = routine;
        Dtc     = dtc;
        OdxImport = odxImport;

        Session.AttachLog(OutputLog);
        Did.AttachLog(OutputLog);
        Routine.AttachLog(OutputLog);
        Dtc.AttachLog(OutputLog);
    }

    /// <summary>
    /// Backward-compat ctor for tests + non-DI callers. Constructs a
    /// minimal <see cref="OdxImportViewModel"/> backed by a stub
    /// <see cref="Services.IOdxImportService"/>; ODX imports won't
    /// affect the actual databases in this mode.
    /// </summary>
    public UdsViewModel(
        SessionPanelViewModel session,
        DidPanelViewModel     did,
        RoutinePanelViewModel routine,
        DtcPanelViewModel     dtc)
        : this(session, did, routine, dtc,
              new OdxImportViewModel(new StubOdxImportService())) { }

    private sealed class StubOdxImportService : Services.IOdxImportService
    {
        public Task<Core.Uds.Odx.OdxImportResult> ImportAsync(
            string odxPath, CancellationToken ct = default)
            => Task.FromResult(Core.Uds.Odx.OdxImportResult.Ok(0, 0, 0, System.Array.Empty<string>()));
    }

    /// <summary>
    /// Open file dialog for .odx / .pdx, run ODX import, refresh all
    /// three database-backed panels (DID / Routine / DTC).
    /// </summary>
    [RelayCommand]
    private async Task LoadOdxAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load ODX diagnostic description",
            Filter = "ODX files (*.odx;*.pdx;*.odx-d)|*.odx;*.pdx;*.odx-d|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        await OdxImport.ImportAsync(dialog.FileName);
        // v2.0.6 PATCH Bug-1: refresh all three database-backed panels
        // after ODX import. Previously only Dtc.RefreshFromDatabase()
        // was called — Did and Routine panels populated their
        // ObservableCollection only in the constructor, so an ODX
        // import (which calls DidDatabase.AddRange / RoutineDatabase.AddRange
        // in-place) silently mutated the database while leaving the UI
        // frozen on the ctor-time snapshot. The 4-line comment that
        // claimed "Did/Routine panels refresh themselves via their
        // existing Load commands" was incorrect — no such refresh
        // exists. All three panels now have explicit RefreshFromDatabase
        // methods (mirroring DtcPanelViewModel's pre-existing pattern).
        Did.RefreshFromDatabase();
        Routine.RefreshFromDatabase();
        Dtc.RefreshFromDatabase();
    }

    [RelayCommand]
    private void ClearOutput() => OutputLog.Clear();
}
