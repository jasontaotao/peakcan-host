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

    /// <summary>Shared UDS log; all panels append here.</summary>
    public ObservableCollection<UdsLogLine> OutputLog { get; } = new();

    public UdsViewModel(
        SessionPanelViewModel session,
        DidPanelViewModel     did,
        RoutinePanelViewModel routine,
        DtcPanelViewModel     dtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(did);
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(dtc);

        Session = session;
        Did     = did;
        Routine = routine;
        Dtc     = dtc;

        Session.AttachLog(OutputLog);
        Did.AttachLog(OutputLog);
        Routine.AttachLog(OutputLog);
        Dtc.AttachLog(OutputLog);
    }

    [RelayCommand]
    private void ClearOutput() => OutputLog.Clear();
}
