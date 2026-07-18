using System.Windows;
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

    // v3.58.0 PATCH: previously had ApiKeyInput_PasswordChanged to call
    // NotifyCanExecuteChanged on SetApiKeyCommand. Since v3.61.0 the
    // command has no CanExecute (validation moved into method body),
    // so this handler was removed. The PasswordBox is still used as
    // CommandParameter source via ElementName binding.
}
