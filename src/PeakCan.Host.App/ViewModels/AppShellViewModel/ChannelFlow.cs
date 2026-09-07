using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow A: Channel lifecycle (v3.16.9.4 PATCH + earlier).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // P2-1 真拆类（2026-09-06）: connect/disconnect 循环 + 六个连接日志
    // （LogConnectOk/LogConnectFailed/LogConnectThrew/LogUnregisterFailed/
    // LogDisconnectOk/LogDisconnectThrew）移交 ChannelConnectionCoordinator
    //（Services/ChannelConnectionCoordinator.cs，同文本重声明）；本 partial
    // 保留探测/枚举、_isConnecting 守卫、UI 文本映射与 OnReadLoopError 处理器。
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ConnectAsync -> OnReadLoopError (intra-flow, passed to coordinator as sink)
    //   - ConnectAsync -> LogProbeOk/LogProbeThrew (Flow D)
    //   - OnReadLoopError -> LogReadLoopError (the 11th helper, lives here with its caller)
    //
    // Required usings: Microsoft.Extensions.Logging, PeakCan.HIL.Core (BaudRate, ChannelInfo),
    // PeakCan.Host.Infrastructure.Statistics (BaudRateMap), PeakCan.Host.Core (DefaultHandle 等)

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
                // review 2026-08-29 P2: 探测成功即认领 ChannelInfo，连接资格的真源统一为
                // SelectedChannel（多通道路径相同），取代 CanConnect 对 ChannelList 文案的
                // "USB1" 字符串哨兵。v1.5.0 持久化语义不变（真实选中句柄落盘）。
                SelectedChannel = new ChannelInfo(DefaultHandle, "USB1");
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

    // v0.4.0: CanConnect requires a probed channel. review 2026-08-29 P2:
    // the legacy "USB1 ..." string sentinel on ChannelList is gone — the
    // legacy probe-success path now claims SelectedChannel, the same source
    // of truth the multi-channel path uses, so failure/default status texts
    // can never re-enable Connect.
    private bool CanConnect() => !IsConnected && !_isConnecting && SelectedChannel is not null;

    /// <summary>
    /// review 2026-08-29 P2: Connect 多路循环进行中 IsConnected 会随首个成功槽位翻
    /// true，从而放行 Disconnect 并发清空 ChannelConnections——Connect 收尾又把
    /// SendService/"已连接 N 路" 写回，用户点了断开却以连接态收场。进行中双向互斥。
    /// </summary>
    private bool _isConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _isConnecting = true;
        NotifyConnectionStateChanged();
        try
        {
            await ConnectCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _isConnecting = false;
            NotifyConnectionStateChanged();
        }
    }

    private async Task ConnectCoreAsync()
    {
        // Task 3 (phase 2 A-3): best-effort multi-channel connect. Walk the
        // pending configs (from IConnectSettingsSink.ApplyConnections); each
        // group connects independently — a failure marks that slot red and
        // continues, never blocking the rest. The legacy single-group path
        // (DIM default → 1-element list) is behaviorally equivalent to the
        // pre-T3 single-channel connect.
        // P2-1 真拆类（2026-09-06）：连接循环本体移交
        // ChannelConnectionCoordinator.ConnectAllAsync；本方法保留 UI 文本映射
        //（开始文本 + 结束聚合文本，最终文本行为与拆分前逐字一致）。
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

        var result = await _coordinator.ConnectAllAsync(configs).ConfigureAwait(true);

        // 结束聚合文本（与拆分前等价）：
        //   count>0 → StatusMessage="已连接 N 路"（覆盖 per-slot 失败文本）；
        //   count==0 且有失败槽 → 保留最后一个失败槽的诊断文本；
        //   count==0 且无失败槽（全 null 组）→ 不覆盖"正在连接..."。
        var count = result.ConnectedCount;
        ConnectionState = count > 0 ? $"已连接 {count} 路" : "已断开";
        if (count > 0)
            StatusMessage = $"已连接 {count} 路";
        else if (result.LastFailureText is not null)
            StatusMessage = result.LastFailureText;
        NotifyConnectionStateChanged();
    }

    /// <summary>
    /// Task 3 (C6 ruling): IsConnected is now a computed property (no
    /// [ObservableProperty] setter), so the Connect/Disconnect CanExecute
    /// chain the old source-gen property carried must be refreshed manually
    /// whenever ChannelConnections changes. Called at the end of Connect/
    /// Disconnect (and now also from per-slot StateChanged via H1 fix).
    /// Cheap (4 notifications).
    /// P1-2（2026-09-06）: 本方法是连接状态变化的统一入口，在此一并向
    /// <see cref="IConnectedChannelsSource"/> publish 快照（HilViewModel 消费）。
    /// P2-1 真拆类（2026-09-06）: per-slot StateChanged 经 coordinator 的
    /// ConnectionsChanged 事件汇入本方法（行订阅内置，不再手动挂退）。
    /// </summary>
    private void NotifyConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        PublishConnectedChannels();
    }

    /// <summary>
    /// P1-2（2026-09-06）: 计算当前已连接通道快照并发布到
    /// <see cref="IConnectedChannelsSource"/>（HilViewModel 读 .Current）。
    /// source 为 null（测试构造点未传）时 no-op。
    /// 线程约束：本方法枚举 ChannelConnections（UI 线程所有）——当前所有
    /// NotifyConnectionStateChanged 调用点都在 UI 线程；若未来出现后台
    /// StateChanged 发布者，必须先封送到 UI 线程再触发本路径。
    /// </summary>
    private void PublishConnectedChannels()
    {
        if (_connectedChannelsSource is null) return;
        _connectedChannelsSource.Publish(ChannelConnections
            .Where(c => c.State == "已连接")
            .Select(c => new HilViewModel.ConnectedChannel(c.Channel.Id.Handle, c.BaudRate, c.IsFd, c.Name, c.Channel))
            .ToList());
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        // Task 3 (phase 2 A-3): disconnect every connected channel, unregister
        // each from the router, unsubscribe read-loop errors, then clear the
        // collection. Per-channel failures are swallowed (best-effort) so one
        // dead channel does not leave the rest connected. Method name kept as
        // DisconnectAsync so the generated DisconnectCommand binding is stable.
        // P2-1 真拆类（2026-09-06）：循环本体移交 coordinator.DisconnectAllAsync；
        // 本方法保留守卫 + UI 文本。
        if (!IsConnected) return;
        if (_isConnecting) return; // review 2026-08-29 P2: 连接进行中不接受断开（CanExecute 兜底）
        StatusMessage = "正在断开所有通道";
        ConnectionState = "断开中...";
        await _coordinator.DisconnectAllAsync().ConfigureAwait(true);
        ConnectionState = "已断开";
        StatusMessage = "已断开";
        NotifyConnectionStateChanged();
    }

    private bool CanDisconnect() => IsConnected && !_isConnecting;

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