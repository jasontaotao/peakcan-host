using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// v3.52.0 MINOR T10: AI Analysis right-side panel. Embedded as the 3rd
/// TabItem in TraceViewerView's right-side TabControl. Lazily instantiated
/// by <see cref="ViewModels.TraceViewerViewModel.AIPanelContent"/> so the
/// empty-state / active-session XAML tree is only built when the user opens
/// the AI Analysis tab the first time.
/// <para>
/// DataContext is set by the VM to <c>this</c> (the parent
/// <c>TraceViewerViewModel</c>) so all bindings resolve against the same
/// instance that owns <c>CurrentAnalysisSession</c>.
/// </para>
/// </summary>
public partial class TraceViewerViewAIPanel : UserControl
{
    public TraceViewerViewAIPanel() => InitializeComponent();
}