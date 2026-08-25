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
/// <para>
/// <b>State-change notification (review H1 fix):</b> because the shell's
/// <c>IsConnected</c> is a computed property with no source-gen setter, the
/// per-slot DisconnectCommand must tell the shell to re-evaluate it. This
/// class raises <see cref="StateChanged"/> whenever <see cref="State"/>
/// changes (via <c>OnStateChanged</c>); the shell subscribes on Add and
/// calls <c>NotifyConnectionStateChanged()</c> so Connect/Disconnect button
/// CanExecute + <c>IsConnected</c>/<c>IsDisconnected</c> bindings refresh.
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

    /// <summary>Whether this channel connected in CAN-FD mode (spec v3 §3.4 binding).</summary>
    public bool IsFd { get; }

    [ObservableProperty] private string _state = "已连接";

    public ChannelConnection(ICanChannel channel, string name, BaudRate baud, bool isFd)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Name = name;
        BaudRate = baud;
        IsFd = isFd;
    }

    /// <summary>
    /// Raised when <see cref="State"/> changes. The shell subscribes to
    /// refresh its computed <c>IsConnected</c> + Connect/Disconnect
    /// CanExecute (review H1 — per-slot disconnect must not leave the
    /// toolbar buttons stale).
    /// </summary>
    public event Action? StateChanged;

    // Source-gen hook for [ObservableProperty] State: fire the notification
    // event so the shell re-evaluates IsConnected after a per-slot disconnect.
    partial void OnStateChanged(string value) => StateChanged?.Invoke();

    /// <summary>
    /// 单项断开：仅断底层 channel + 置状态。不触碰 shell 集合/SendService
    /// （那是 shell DisconnectAllAsync 的职责，本命令供 UI 每项独立断开按钮）。
    /// 置 State 后通过 <see cref="StateChanged"/> 通知 shell 刷新 IsConnected。
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await Channel.DisconnectAsync().ConfigureAwait(true);
        State = "已断开";
    }
}
