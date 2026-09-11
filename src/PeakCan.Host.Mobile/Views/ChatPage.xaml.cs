using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class ChatPage : ContentPage
{
    private readonly ChatViewModel _vm;

    public ChatPage(ChatViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 启动时恢复已保存的 key 并激活第一个
        _ = _vm.LoadChatSavedKeysAsync();
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        try
        {
            await _vm.SendMessageCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _vm.ConnectionHint = $"发送失败: {ex.Message}";
        }
        ChatEntry.Unfocus();
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
        => _ = Navigation.PushAsync(new ChatSettingsPage(_vm));

    private void OnClearClicked(object? sender, EventArgs e)
        => _vm.ClearChatCommand.Execute(null);
}
