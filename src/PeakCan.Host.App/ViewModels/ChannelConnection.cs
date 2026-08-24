using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Task 3 (phase 2 A-3): one slot in the shell's multi-channel connection list.
/// Wraps a connected <see cref="ICanChannel"/> with its logical name + baud rate
/// + a UI-facing state string + a per-slot Disconnect command.
/// <para>
/// <b>Does not hold a shell reference</b> (C2 ruling): the per-slot
/// DisconnectCommand only calls <see cref="ICanChannel.DisconnectAsync"/> —
/// it does not touch the shell's <c>ChannelConnections</c> collection or
/// <c>SendService</c>. The shell's <c>DisconnectAllAsync</c> walks the
/// collection for the full-teardown path; this command is for the UI's
/// "disconnect just this one channel" button.
/// </para>
/// </summary>
public sealed partial class ChannelConnection : ObservableObject
{
    /// <summary>The underlying CAN channel (owned by the shell for teardown).</summary>
    public ICanChannel Channel { get; }

    /// <summary>Logical channel name (from ChannelInfo.Name or user-entered).</summary>
    public string Name { get; }

    /// <summary>The baud rate this channel connected at.</summary>
    public BaudRate BaudRate { get; }

    [ObservableProperty] private string _state = "已连接";

    public ChannelConnection(ICanChannel channel, string name, BaudRate baud)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Name = name;
        BaudRate = baud;
    }

    /// <summary>
    /// 单项断开：仅断底层 channel + 置状态。不触碰 shell 集合/SendService
    /// （那是 shell DisconnectAllAsync 的职责，本命令供 UI 每项独立断开按钮）。
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await Channel.DisconnectAsync().ConfigureAwait(true);
        State = "已断开";
    }
}
