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
            Filter = "ODX files (*.odx;*.pdx)|*.odx;*.pdx|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        await OdxImport.ImportAsync(dialog.FileName);
        // Refresh panels to reflect ODX-imported definitions.
        Dtc.RefreshFromDatabase();
        // Did/Routine panels refresh themselves via their existing
        // Load commands; future DI wiring exposes explicit refresh.
    }

    [RelayCommand]
    private void ClearOutput() => OutputLog.Clear();
}
