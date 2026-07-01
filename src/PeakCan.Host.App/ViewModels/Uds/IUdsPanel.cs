using System.Collections.ObjectModel;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Internal contract: a panel VM exposes a single AttachLog hook so the
/// orchestrator can wire a shared ObservableCollection at construction
/// time without forcing each panel ctor to take one more parameter.
/// </summary>
internal interface IUdsPanel
{
    void AttachLog(ObservableCollection<UdsLogLine> log);
}
