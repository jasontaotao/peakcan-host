using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the AI Chat panel. Only handles Enter-to-send
/// (Shift+Enter inserts a newline) and PasswordBox sync.
/// </summary>
public partial class TraceViewerViewChatPanel : UserControl
{
    public TraceViewerViewChatPanel()
    {
        InitializeComponent();
    }

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (DataContext is TraceViewerViewModel vm && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    /// <summary>PasswordBox 值同步到 VM (WPF PasswordBox.Password 不可绑定)。</summary>
    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is PasswordBox pb)
            vm.SetChatApiKeyInput(pb.Password);
    }
}