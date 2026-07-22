using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using CoreFlashPipeline = PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Orchestrator for the UDS diagnostic tab. Holds the panel ViewModels
/// (Session / DID / Routine / DTC / Flashing), a shared output log, and a
/// clear-output command. Owns no UdsClient interaction of its own — each
/// panel owns its own RelayCommands and talks to UdsClient directly.
/// </summary>
public sealed partial class UdsViewModel : ObservableObject
{
    public SessionPanelViewModel Session { get; }
    public DidPanelViewModel     Did     { get; }
    public RoutinePanelViewModel Routine { get; }
    public DtcPanelViewModel     Dtc     { get; }
    public OdxImportViewModel    OdxImport { get; }
    /// <summary>
    /// Flashing-tab panel VM (C4). Owns the secondary flash-stack lifecycle + drives
    /// <c>PipelineExecutor</c>. NULL-safe in back-compat ctors — backed by a NoOp
    /// factory that refuses Start so the orchestrator can be constructed without DI
    /// for tests/non-flash callers (parallel to <see cref="StubOdxImportService"/>).
    /// </summary>
    public FlashPanelViewModel Flash { get; }

    /// <summary>Shared UDS log; all panels append here.</summary>
    public ObservableCollection<UdsLogLine> OutputLog { get; } = new();

    /// <summary>
    /// Production ctor (DI). Six panels: Session / DID / Routine / DTC / OdxImport / Flash.
    /// </summary>
    public UdsViewModel(
        SessionPanelViewModel session,
        DidPanelViewModel     did,
        RoutinePanelViewModel routine,
        DtcPanelViewModel     dtc,
        OdxImportViewModel    odxImport,
        FlashPanelViewModel   flash)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(did);
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(dtc);
        ArgumentNullException.ThrowIfNull(odxImport);
        ArgumentNullException.ThrowIfNull(flash);

        Session = session;
        Did     = did;
        Routine = routine;
        Dtc     = dtc;
        OdxImport = odxImport;
        Flash = flash;

        Session.AttachLog(OutputLog);
        Did.AttachLog(OutputLog);
        Routine.AttachLog(OutputLog);
        Dtc.AttachLog(OutputLog);
        // Flash panel has its own status/progress surface; it does NOT append to the
        // shared UDS log — flash output is voluminous (per-block TransferData) and would
        // drown the diagnostic log. Keep the logs separate by design.
    }

    /// <summary>
    /// Backward-compat ctor (6→5 arg, no Flash): constructs a Flash panel backed by a
    /// <see cref="NoOpSecondaryFlashStackFactory"/> that refuses Start, so existing
    /// callers that never flash get a non-null <see cref="Flash"/> without pulling the
    /// full flash-stack DI graph. ODX import is stubbed as before.
    /// </summary>
    public UdsViewModel(
        SessionPanelViewModel session,
        DidPanelViewModel     did,
        RoutinePanelViewModel routine,
        DtcPanelViewModel     dtc,
        OdxImportViewModel    odxImport)
        : this(session, did, routine, dtc, odxImport,
              new FlashPanelViewModel(
                  new NoOpSecondaryFlashStackFactory(),
                  NullLogger<FlashPanelViewModel>.Instance)) { }

    /// <summary>
    /// Backward-compat ctor for tests + non-DI callers. Constructs a minimal
    /// <see cref="OdxImportViewModel"/> backed by a stub
    /// <see cref="Services.IOdxImportService"/> AND a NoOp flash panel;
    /// ODX imports won't affect the actual databases and flashing is refused.
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
    /// Back-compat flash-stack factory that refuses <see cref="Build"/> — lets a
    /// <see cref="UdsViewModel"/> be constructed without the full flash DI graph (tests,
    /// non-flash callers) while keeping <see cref="Flash"/> non-null. Build is only
    /// reached if an operator presses Start on the back-compat orchestrator, which is a
    /// programming error (the production ctor is the only one wired to flash in DI).
    /// </summary>
    private sealed class NoOpSecondaryFlashStackFactory : ISecondaryFlashStackFactory
    {
        public ISecondaryFlashStack Build(CoreFlashPipeline.FlashStepSnapshot securityStep, FlashProfile profile)
            => throw new NotImplementedException(
                "Flash is not available — this UdsViewModel was constructed via the " +
                "back-compat ctor without a real flash-stack factory. Use the DI ctor " +
                "(with FlashPanelViewModel) to enable flashing.");
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
