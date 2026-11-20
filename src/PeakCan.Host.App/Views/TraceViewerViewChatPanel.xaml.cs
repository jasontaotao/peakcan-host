using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the AI Chat panel. Handles Enter-to-send
/// (Shift+Enter inserts a newline), PasswordBox sync, and the focus
/// hand-off from the NM panel's “让 AI 解读” jump.
/// </summary>
public partial class TraceViewerViewChatPanel : UserControl
{
    private TraceViewerViewModel? _vm;

    /// <summary>
    /// NM 面板预填问题时聊天页可能还没被选中：此时元素不可聚焦。
    /// 记下待办，等本控件变为可见（Tab 切换完成）再执行。
    /// </summary>
    private bool _focusPending;

    public TraceViewerViewChatPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => HookVm();
        DataContextChanged += (_, _) => HookVm();
        Unloaded += (_, _) => UnhookVm();
        IsVisibleChanged += (_, _) => TryFocusPending();
    }

    private void HookVm()
    {
        UnhookVm();
        _vm = DataContext as TraceViewerViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void UnhookVm()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TraceViewerViewModel.ChatInputFocusTicket)) return;
        _focusPending = true;
        TryFocusPending();
    }

    /// <summary>控件可见时才真正聚焦；不可见时保持待办，等 IsVisibleChanged 再来。</summary>
    private void TryFocusPending()
    {
        if (!_focusPending || !IsVisible) return;
        _focusPending = false;
        // Tab 切换后的布局/模板应用可能还没完成，延后到输入优先级再聚焦。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            ChatInputBox.Focus();
            ChatInputBox.CaretIndex = ChatInputBox.Text.Length;
        }));
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
