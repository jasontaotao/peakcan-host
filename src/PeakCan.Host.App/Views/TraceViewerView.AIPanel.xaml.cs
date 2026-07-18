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

    /// <summary>
    /// v3.61.0 PATCH: sync PasswordBox content to VM.PendingApiKeyValue.
    /// WPF PasswordBox.Password cannot be reliably read via CommandParameter
    /// ElementName binding — this handler stores it in a VM string property
    /// that the parameterless SetApiKeyCommand reads instead.
    /// </summary>
    private void ApiKeyInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.TraceViewerViewModel vm)
        {
            vm.PendingApiKeyValue = ((PasswordBox)sender).Password;
        }
    }
}
