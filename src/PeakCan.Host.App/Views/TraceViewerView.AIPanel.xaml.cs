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
    /// W40 P2 PATCH: refresh the SetApiKeyCommand CanExecute whenever
    /// the PasswordBox content changes — CanExecute is computed from
    /// <c>!string.IsNullOrWhiteSpace(value)</c> and WPF does not auto-
    /// raise CommandManager.RequerySuggested on PasswordBox.Password
    /// property changes (unlike TextBox.Text).
    /// </summary>
    private void ApiKeyInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.TraceViewerViewModel vm)
        {
            vm.SetApiKeyCommand.NotifyCanExecuteChanged();
        }
    }
}