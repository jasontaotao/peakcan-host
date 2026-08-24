using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow A: Channel lifecycle (v3.16.9.4 PATCH + earlier).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ConnectAsync -> OnReadLoopError (intra-flow subscription)
    //   - ConnectAsync -> LogProbeOk/LogProbeThrew/LogConnectOk/LogConnectFailed/LogConnectThrew/LogUnregisterFailed (Flow D + this file's 11th helper)
    //   - DisconnectAsync -> LogDisconnectOk/LogDisconnectThrew (Flow D)
    //   - OnReadLoopError -> LogReadLoopError (the 11th helper, lives here with its caller)
    //
    // Required usings: Microsoft.Extensions.Logging, PeakCan.HIL.Core (ErrorCode, ReadLoopError, ReadLoopErrorKind, BaudRate),
    // PeakCan.Host.Infrastructure.Channel (ChannelRouter, ChannelId, IChannelProbe, IChannelFactory, IChannelEnumerator)

    /// <summary>
    /// IsFd 属性变更回调：切换模式时自动将 SelectedBaudRate 重置为对应列表首项，
    /// 避免用户在 Classic 模式下残留一个 FD 预设（或反之）。
    /// CommunityToolkit.Mvvm 源生成器会将此方法注册到 IsFd 的 setter 中。
    /// </summary>
    partial void OnIsFdChanged(bool value)
    {
        SelectedBaudRate = value ? BaudRate.CanFd1Mbps : BaudRate.Can1Mbps;
    }

    /// <summary>
    /// v1.5.0 MINOR: persist <c>SelectedChannel.Handle</c> to
    /// <c>Channel:SelectedHandle</c> in <see cref="IConfiguration"/> so the
    /// next process restart can restore the previously-selected channel
    /// after EnumerateChannels populates <see cref="AvailableChannels"/>.
    /// Handle format is uppercase hex without 0x prefix (matches PEAK
    /// convention: 0x51 → "51"). A null SelectedChannel clears the key.
    /// <para>
    /// v1.5.0 review fix: when <see cref="EnumerateChannels"/> auto-selects
    /// a fallback (the persisted handle did not match any enumerated channel),
    /// <see cref="_suppressNextPersist"/> is set so this write is skipped,
    /// preserving the user's original persisted value across the hardware
    /// mismatch. Any subsequent user-driven selection always persists.
    /// </para>
    /// </summary>
    partial void OnSelectedChannelChanged(ChannelInfo? value)
    {
        if (_suppressNextPersist)
        {
            // Consume the flag for this single auto-select event; the very
            // next user-driven change will persist normally.
            _suppressNextPersist = false;
            return;
        }
        _configuration["Channel:SelectedHandle"] = value?.Handle.ToString("X2");
    }

    [RelayCommand(CanExecute = nameof(CanEnumerateChannels))]
    private void EnumerateChannels()
    {
        // v0.4.0: if IChannelEnumerator is available, probe all channels;
        // otherwise fall back to the single-channel IChannelProbe path.
        if (_channelEnumerator is not null)
        {
            var channels = _channelEnumerator.Enumerate();
            AvailableChannels = channels;
            if (channels.Count > 0)
            {
                // v1.5.0 MINOR: if the user previously selected a different
                // channel and that channel is still present in the
                // enumerated list, restore it. Otherwise fall back to the
                // v0.4.0 default (channels[0]).
                var persisted = _persistedHandleOnStartup;
                _persistedHandleOnStartup = null; // consume once
                var match = persisted.HasValue
                    ? channels.FirstOrDefault(c => c.Handle == persisted.Value)
                    : null;
                // v1.5.0 review fix: when the persisted handle did not
                // match any enumerated channel (e.g. "99" but only 0x51/0x52
                // present), the auto-select below would otherwise trigger
                // OnSelectedChannelChanged and overwrite the user's persisted
                // "99" with "51". Suppress that one write so the user's
                // original intent survives across hardware changes.
                if (persisted.HasValue && match is null)
                {
                    _suppressNextPersist = true;
                }
                SelectedChannel = match ?? channels[0];
                ChannelList = $"{SelectedChannel.Name} ({SelectedBaudRate.Name})";
                StatusMessage = $"检测到 {channels.Count} 个通道";
                LogProbeOk(_logger, SelectedChannel.Handle);
            }
            else
            {
                SelectedChannel = null;
                ChannelList = "未检测到 PEAK 硬件";
                StatusMessage = "未找到通道";
                LogProbeThrew(_logger, DefaultHandle,
                    new InvalidOperationException("No channels found"));
            }
        }
        else
        {
            // Legacy single-channel path (tests without IChannelEnumerator).
            var result = _channelProbe.Probe(DefaultHandle);
            if (result.Ok)
            {
                ChannelList = $"USB1 ({SelectedBaudRate.Name})";
                StatusMessage = result.Message;
                LogProbeOk(_logger, DefaultHandle);
            }
            else
            {
                ChannelList = $"未检测到 PEAK 硬件: {result.Message}";
                StatusMessage = result.Message;
                LogProbeThrew(_logger, DefaultHandle,
                    new InvalidOperationException(result.Message));
            }
        }
    }

    private bool CanEnumerateChannels() => !IsConnected;

    // v0.4.0: CanConnect now checks SelectedChannel when available,
    // falling back to the legacy ChannelList string check.
    private bool CanConnect() => !IsConnected && (
        SelectedChannel is not null
        || !string.IsNullOrEmpty(ChannelList));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // Task 3 (phase 2 A-3): best-effort multi-channel connect. Walk the
        // pending configs (from IConnectSettingsSink.ApplyConnections); each
        // group connects independently — a failure marks that slot red and
        // continues, never blocking the rest. The legacy single-group path
        // (DIM default → 1-element list) is behaviorally equivalent to the
        // pre-T3 single-channel connect.
        var configs = _pendingConfigs;
        // 零回归兜底：旧单通道路径（工具栏直接 Connect，未走 ApplyConnections）→
        // _pendingConfigs 空。回落到用 SelectedChannel（或 DefaultHandle 当未 probe）
        // 构造单元素列表，行为等价旧 ConnectAsync（连 SelectedChannel + BaudRate + IsFd；
        // SelectedChannel null 时旧码用 DefaultHandle，这里同样）。
        if (configs.Count == 0)
        {
            var legacyCh = SelectedChannel ?? new ChannelInfo(DefaultHandle, "USB1");
            configs = new[] { new ConnectionConfig(legacyCh, SelectedBaudRate, IsFd) };
        }
        ConnectionState = "连接中...";
        StatusMessage = configs.Count > 1
            ? $"正在连接 {configs.Count} 路 CAN..."
            : $"正在连接 {SelectedChannel?.Name ?? "USB1"} ({SelectedBaudRate.Name})";

        foreach (var cfg in configs)
        {
            if (cfg.Channel is null) continue; // null 组跳过
            var handle = cfg.Channel.Handle;
            var rate = cfg.BaudRate;
            var channel = _channelFactory.Create(new ChannelId(handle));
            try
            {
                var result = await channel.ConnectAsync(rate, fd: cfg.IsFd).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    _router.RegisterChannel(channel);
                    // v3.16.9.4 PATCH: subscribe to read-loop errors so bus-off /
                    // driver unload / hardware faults surface on the UI status
                    // bar. Event fires on the SDK read thread; the handler must
                    // marshal to the UI thread itself (we use the captured sync
                    // context to marshal back onto the UI thread).
                    channel.ReadLoopError += OnReadLoopError;
                    ChannelConnections.Add(new ChannelConnection(channel, cfg.Channel.Name, rate));
                    LogConnectOk(_logger, handle);
                }
                else
                {
                    // 尽力式：该组标红跳过，不阻塞其余组。
                    var err = result.Error!;
                    ChannelConnections.Add(new ChannelConnection(channel, cfg.Channel.Name, rate)
                        { State = $"连接失败: {err.Code}" });
                    StatusMessage = $"通道 {cfg.Channel.Name} 连接失败: {err.Code} {err.Message}";
                    LogConnectFailed(_logger, handle, err.Code, err.Message);
                    // PeakCanChannel ctor allocates a CancellationTokenSource
                    // (used by the read loop). On a failed Connect the channel
                    // never acquires the hardware, so the safe teardown is to
                    // dispose it now rather than wait for GC.
                    await channel.DisposeAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                // 尽力式：该组标红，继续其余组。
                ChannelConnections.Add(new ChannelConnection(channel, cfg.Channel.Name, rate)
                    { State = $"连接异常: {ex.GetType().Name}" });
                StatusMessage = $"通道 {cfg.Channel.Name} 连接异常: {ex.GetType().Name}";
                LogConnectThrew(_logger, handle, ex);
                // RegisterChannel 抛异常时硬件可能已连接但未注册——先
                // 断开硬件连接再 Unregister + Dispose，避免 handle 泄漏
                // （review M2 fix：DisposeAsync 不保证断开硬件连接）。
                try { await channel.DisconnectAsync().ConfigureAwait(true); }
                catch (Exception discEx) { LogDisconnectThrew(_logger, handle, discEx); }
                try { _router.UnregisterChannel(channel); }
                catch (Exception unregEx)
                {
                    LogUnregisterFailed(_logger, handle, unregEx);
                }
                await channel.DisposeAsync().ConfigureAwait(true);
            }
        }

        // Publish the connected set to SendService (default target = first
        // connected channel) and refresh the derived IsConnected + CanExecute.
        var connected = ChannelConnections.Where(c => c.State == "已连接").ToList();
        _sendService.SetChannels(connected.ToDictionary(c => c.Channel.Id, c => c.Channel));
        _sendService.ActiveChannel = connected.FirstOrDefault()?.Channel;
        var count = connected.Count;
        ConnectionState = count > 0 ? $"已连接 {count} 路" : "已断开";
        // 仅在至少一路成功时覆盖 StatusMessage；全失败时保留 catch/else 块
        // 已设的 per-channel 错误消息（避免抹掉"连接异常/连接失败"诊断信息）。
        if (count > 0)
            StatusMessage = $"已连接 {count} 路";
        NotifyConnectionStateChanged();
    }

    /// <summary>
    /// Task 3 review H1 fix: subscribe to each slot's StateChanged on Add,
    /// unsubscribe on Remove/Clear. A per-slot disconnect changes State, which
    /// fires StateChanged, which tells the shell to re-evaluate IsConnected +
    /// refresh Connect/Disconnect CanExecute — so the toolbar buttons stay in
    /// sync even when only one channel is disconnected via its own button.
    /// </summary>
    private void OnChannelConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } newItems)
            foreach (ChannelConnection c in newItems)
                c.StateChanged += NotifyConnectionStateChanged;
        if (e.OldItems is { } oldItems)
            foreach (ChannelConnection c in oldItems)
                c.StateChanged -= NotifyConnectionStateChanged;
    }

    /// <summary>
    /// Task 3 (C6 ruling): IsConnected is now a computed property (no
    /// [ObservableProperty] setter), so the Connect/Disconnect CanExecute
    /// chain the old source-gen property carried must be refreshed manually
    /// whenever ChannelConnections changes. Called at the end of Connect/
    /// Disconnect (and now also from per-slot StateChanged via H1 fix).
    /// Cheap (4 notifications).
    /// </summary>
    private void NotifyConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        // Task 3 (phase 2 A-3): disconnect every connected channel, unregister
        // each from the router, unsubscribe read-loop errors, then clear the
        // collection. Per-channel failures are swallowed (best-effort) so one
        // dead channel does not leave the rest connected. Method name kept as
        // DisconnectAsync so the generated DisconnectCommand binding is stable.
        if (!IsConnected) return;
        StatusMessage = "正在断开所有通道";
        ConnectionState = "断开中...";
        var snapshot = ChannelConnections.ToList();
        foreach (var conn in snapshot)
        {
            try
            {
                await conn.Channel.DisconnectAsync().ConfigureAwait(true);
                LogDisconnectOk(_logger, conn.Channel.Id.Handle);
            }
            catch (Exception ex)
            {
                // DisconnectAsync swallows hardware failures per its own
                // contract; surface the exception as a per-channel state so
                // the operator sees which channel failed to disconnect.
                conn.State = $"断开异常: {ex.GetType().Name}";
                LogDisconnectThrew(_logger, conn.Channel.Id.Handle, ex);
            }
            try { _router.UnregisterChannel(conn.Channel); }
            catch (Exception unregEx) { LogUnregisterFailed(_logger, conn.Channel.Id.Handle, unregEx); }
            // v3.16.9.4 PATCH: unsubscribe read-loop errors before dropping
            // the reference — match the source-gen delegate equality so the
            // old channel's event does not pin this VM.
            conn.Channel.ReadLoopError -= OnReadLoopError;
            conn.State = "已断开";
        }
        ChannelConnections.Clear();
        _sendService.SetChannels(null);
        _sendService.ActiveChannel = null;
        ConnectionState = "已断开";
        StatusMessage = "已断开";
        NotifyConnectionStateChanged();
    }

    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// v3.16.9.4 PATCH: handler for <see cref="ICanChannel.ReadLoopError"/>.
    /// Fires on the SDK read thread; we marshal to the UI thread by setting
    /// <see cref="StatusMessage"/> via the [ObservableProperty] source-gen
    /// setter (which raises PropertyChanged on the captured sync context —
    /// or directly if no sync context).
    /// <para>
    /// The handler does NOT auto-disconnect — bus-off is often transient
    /// (PCANBasic automatically re-enters ERROR_ACTIVE after the bus
    /// recovers). Surfacing the error gives the operator the information
    /// to decide; the read loop's existing MaxConsecutiveReadFailures=100
    /// give-up mechanism handles the genuinely-dead-bus case.
    /// </para>
    /// </summary>
    private void OnReadLoopError(ReadLoopError err)
    {
        var msg = err.Kind switch
        {
            ReadLoopErrorKind.ClassicReadException =>
                $"Read loop error (classic): {err.Exception?.Message ?? "(no exception)"} — bus may be off",
            ReadLoopErrorKind.FdReadException =>
                $"Read loop error (FD): {err.Exception?.Message ?? "(no exception)"} — driver may be unloaded",
            ReadLoopErrorKind.LoopGivingUp =>
                $"Read loop abandoned after 100 failures — call Disconnect + Connect to recover",
            _ => $"Read loop error: kind={err.Kind}",
        };
        // Mark StatusMessage as the error message; the toolbar binding picks
        // it up. (YAGNI for a separate red-color binding — the StatusMessage
        // already conveys the error and the operator can correlate with the
        // "connected but no frames" symptom.)
        StatusMessage = msg;
        ConnectionState = $"Connected (read loop degraded: {err.Kind})";
        LogReadLoopError(_logger, err.Handle, err.Kind.ToString(), err.Exception);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read loop error surfaced to UI: handle=0x{Handle:X2} kind={Kind}")]
    private static partial void LogReadLoopError(ILogger logger, ushort handle, string kind, Exception? ex);
}