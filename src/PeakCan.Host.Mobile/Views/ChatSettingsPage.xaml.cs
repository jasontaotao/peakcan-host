using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class ChatSettingsPage : ContentPage
{
    private readonly ChatViewModel _vm;

    public ChatSettingsPage(ChatViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
    }

    private async void OnTestAndSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            await _vm.TestAndSaveChatKeyCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _vm.ChatConnectionStatus = $"保存失败: {ex.Message}";
        }
    }

    private void OnResetClicked(object? sender, EventArgs e)
        => _vm.ResetChatConfigCommand.Execute(null);

    private async void OnSwitchClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is SavedKeyInfo info)
            await _vm.SwitchChatKeyCommand.ExecuteAsync(info);
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is SavedKeyInfo info)
            await _vm.DeleteChatKeyCommand.ExecuteAsync(info);
    }
}
