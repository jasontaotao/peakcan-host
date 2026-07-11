using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
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
    // Required usings: Microsoft.Extensions.Logging, PeakCan.Host.Core (ErrorCode, ReadLoopError, ReadLoopErrorKind, BaudRate),
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
                StatusMessage = $"Detected {channels.Count} channel(s)";
                LogProbeOk(_logger, SelectedChannel.Handle);
            }
            else
            {
                SelectedChannel = null;
                ChannelList = "No PEAK hardware detected";
                StatusMessage = "No channels found";
                LogProbeThrew(_logger, PcanUsbFdFirstHandle,
                    new InvalidOperationException("No channels found"));
            }
        }
        else
        {
            // Legacy single-channel path (tests without IChannelEnumerator).
            var result = _channelProbe.Probe(PcanUsbFdFirstHandle);
            if (result.Ok)
            {
                ChannelList = $"USB1 ({SelectedBaudRate.Name})";
                StatusMessage = result.Message;
                LogProbeOk(_logger, PcanUsbFdFirstHandle);
            }
            else
            {
                ChannelList = $"No PEAK hardware detected: {result.Message}";
                StatusMessage = result.Message;
                LogProbeThrew(_logger, PcanUsbFdFirstHandle,
                    new InvalidOperationException(result.Message));
            }
        }
    }

    private bool CanEnumerateChannels() => !IsConnected;

    // v0.4.0: CanConnect now checks SelectedChannel when available,
    // falling back to the legacy ChannelList string check.
    private bool CanConnect() => !IsConnected && (
        SelectedChannel is not null
        || ChannelList.StartsWith("USB1", StringComparison.Ordinal));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // v0.4.0: use SelectedChannel handle when available.
        var handle = SelectedChannel?.Handle ?? PcanUsbFdFirstHandle;
        ConnectionState = "Connecting...";
        StatusMessage = $"Connecting to {SelectedChannel?.Name ?? "USB1"} ({SelectedBaudRate.Name})";
        var channel = _channelFactory.Create(new ChannelId(handle));
        try
        {
            var result = await channel.ConnectAsync(SelectedBaudRate, fd: IsFd).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _activeChannel = channel;
                _router.RegisterChannel(channel);
                // v3.16.9.4 PATCH: subscribe to read-loop errors so bus-off /
                // driver unload / hardware faults surface on the UI status
                // bar. Event fires on the SDK read thread; the handler must
                // marshal to the UI thread itself (we use the captured sync
                // context — same pattern as TraceViewerViewModel.OnAnyFrameEmitted).
                channel.ReadLoopError += OnReadLoopError;
                // Set IsConnected=true BEFORE publishing the channel to
                // SendService so that any binding observer sees "connected"
                // and an available channel atomically — no window where
                // Send can fire against a channel the UI still considers
                // disconnected. [ObservableProperty] setters fire
                // PropertyChanged in order; this ordering keeps the
                // Send button's CanExecute (when wired) consistent.
                IsConnected = true;
                ConnectionState = $"Connected to {SelectedChannel?.Name ?? "USB1"} ({SelectedBaudRate.Name})";
                StatusMessage = "Connected";
                _sendService.ActiveChannel = channel;
                LogConnectOk(_logger, handle);
            }
            else
            {
                ConnectionState = "Disconnected";
                _sendService.ActiveChannel = null;
                var err = result.Error!;
                StatusMessage = $"Connect failed: {err.Code} {err.Message}";
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
            ConnectionState = "Disconnected";
            StatusMessage = $"Connect exception: {ex.GetType().Name}";
            LogConnectThrew(_logger, handle, ex);
            // v3.8.8 PATCH F1: also unregister the channel from the
            // router. RegisterChannel is a two-step operation in
            // ChannelRouter (Add to _channels + event subscribe); if
            // the subscribe step throws AFTER the Add, the channel
            // stays in the router's sink list and frames keep fanning
            // into a disposed sink. UnregisterChannel is idempotent
            // (Remove is a no-op if the channel was never added), so
            // it is safe to call on every catch. Best-effort wrapped
            // so a router failure cannot prevent the channel dispose.
            try { _router.UnregisterChannel(channel); }
            catch (Exception unregEx)
            {
                LogUnregisterFailed(_logger, handle, unregEx);
            }
            // M1 fix: dispose the channel if RegisterChannel or any
            // subsequent step threw after ConnectAsync succeeded. Without
            // this, the channel (and its CTS + read-loop task) leaks until
            // the next GC cycle.
            await channel.DisposeAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (!IsConnected || _activeChannel is null) return;
        StatusMessage = $"Disconnecting from {SelectedBaudRate.Name}";
        ConnectionState = "Disconnecting...";
        try
        {
            await _activeChannel.DisconnectAsync().ConfigureAwait(true);
            _router.UnregisterChannel(_activeChannel);
            // v3.16.9.4 PATCH: unsubscribe from read-loop errors before
            // dropping the channel reference. Without this, a subsequent
            // Connect → Disconnect cycle would leave the old channel's
            // ReadLoopError event holding a strong reference to this VM
            // (closure pins the captured `this`). Match the source-gen
            // delegate equality used by PeakCanChannel.ReadLoopError.
            _activeChannel.ReadLoopError -= OnReadLoopError;
            _sendService.ActiveChannel = null;
            IsConnected = false;
            ConnectionState = "Disconnected";
            StatusMessage = "Disconnected";
            LogDisconnectOk(_logger, PcanUsbFdFirstHandle);
        }
        catch (Exception ex)
        {
            // DisconnectAsync swallows hardware failures per its own contract;
            // any exception here is therefore unexpected. Surface it as a
            // status message so the operator is not stuck in "Disconnecting".
            // Reset every piece of state the success path resets: leaving
            // IsConnected=true keeps the Disconnect button enabled against a
            // dead channel; leaving the channel on the router keeps frames
            // being routed to it; leaving SendService.ActiveChannel set
            // targets the next manual send at a dead channel. Order matches
            // the success path (UnregisterChannel → ActiveChannel=null →
            // IsConnected=false) so the two paths produce identical state
            // transitions from an observer's point of view.
            _router.UnregisterChannel(_activeChannel);
            _sendService.ActiveChannel = null;
            IsConnected = false;
            ConnectionState = "Disconnected";
            StatusMessage = $"Disconnect exception: {ex.GetType().Name}";
            LogDisconnectThrew(_logger, PcanUsbFdFirstHandle, ex);
        }
        finally
        {
            _activeChannel = null;
        }
    }

    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// v3.16.9.4 PATCH: handler for <see cref="ICanChannel.ReadLoopError"/>.
    /// Fires on the SDK read thread; we marshal to the UI thread by setting
    /// <see cref="StatusMessage"/> via the [ObservableProperty] source-gen
    /// setter (which raises PropertyChanged on the captured sync context —
    /// or directly if no sync context, matching the same pattern as
    /// <see cref="TraceViewerViewModel.OnAnyFrameEmitted"/>).
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