using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Exposes CAN bus operations to the JavaScript scripting engine.
/// Provides functions for sending frames, registering callbacks for
/// received frames, and querying channel state.
/// <para>
/// <b>Thread-safety:</b> Callbacks are stored in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and can be
/// registered/unregistered from any thread. Frame callbacks are
/// invoked on the V8 engine thread (the caller of
/// <see cref="OnFrameReceived"/> is responsible for marshaling).
/// </para>
/// </summary>
public sealed partial class CanApi : IFrameSink, IScriptCanApi
{
    private readonly ILogger<CanApi> _logger;
    private readonly SendService _sendService;
    private readonly ChannelRouter _channelRouter;

    // Callbacks registered via can.onFrame().
    private readonly ConcurrentDictionary<string, Action<CanFrame>> _frameCallbacks = new();

    // Callbacks registered via can.onMessage() keyed by CAN ID.
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<string, Action<CanFrame>>> _messageCallbacks = new();

    // Callbacks registered via can.onMessage() keyed by hex prefix.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Action<CanFrame>>> _prefixCallbacks = new();

    private int _callbackCounter;

    public CanApi(
        ILogger<CanApi> logger,
        SendService sendService,
        ChannelRouter channelRouter)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sendService);
        ArgumentNullException.ThrowIfNull(channelRouter);

        _logger = logger;
        _sendService = sendService;
        _channelRouter = channelRouter;

        // Subscribe to the channel router to receive all frames.
        _channelRouter.AttachSink(this);
    }

    /// <summary>
    /// Send a CAN frame.
    /// </summary>
    /// <param name="id">CAN ID (11-bit or 29-bit).</param>
    /// <param name="data">Raw data bytes (max 8 for classic, 64 for FD).</param>
    /// <param name="fd">If true, send as CAN FD frame.</param>
    /// <param name="extended">If true, use 29-bit extended ID format.</param>
    /// <returns>True if the frame was sent successfully.</returns>
    public async Task<bool> Send(int id, byte[] data, bool fd = false, bool extended = false)
    {
        if (id < 0 || id > 0x1FFFFFFF)
        {
            LogInvalidCanId(_logger, id);
            return false;
        }

        if (data is null || data.Length == 0)
        {
            LogSendEmptyData(_logger);
            return false;
        }

        int maxDlc = fd ? 64 : 8;
        if (data.Length > maxDlc)
        {
            LogDataTooLong(_logger, data.Length, maxDlc, fd ? "FD" : "classic");
            return false;
        }

        var format = extended ? FrameFormat.Extended : FrameFormat.Standard;
        var canId = new CanId((uint)id, format);
        var flags = fd ? FrameFlags.Fd : FrameFlags.None;
        var frame = new CanFrame(canId, data, flags, default, default);

        var result = await _sendService.SendAsync(frame).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogSendFailed(_logger, result.Error?.Message ?? "Unknown error");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Register a callback for all received frames.
    /// Returns a callback ID that can be used with <see cref="OffFrame"/>.
    /// </summary>
    /// <param name="callback">JavaScript function to call for each frame.</param>
    /// <returns>Callback ID string.</returns>
    public string OnFrame(Action<CanFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var id = $"frame_{Interlocked.Increment(ref _callbackCounter)}";
        _frameCallbacks[id] = callback;
        LogFrameCallbackRegistered(_logger, id);
        return id;
    }

    /// <summary>
    /// Unregister a frame callback.
    /// </summary>
    /// <param name="callbackId">Callback ID returned by <see cref="OnFrame"/>.</param>
    public void OffFrame(string callbackId)
    {
        if (callbackId is null) return;
        if (_frameCallbacks.TryRemove(callbackId, out _))
        {
            LogFrameCallbackUnregistered(_logger, callbackId);
        }
    }

    /// <summary>
    /// Register a callback for frames with a specific CAN ID.
    /// </summary>
    /// <param name="id">Exact CAN ID to match, or hex prefix string (e.g., "1A" matches 0x1A0-0x1AF).</param>
    /// <param name="callback">JavaScript function to call for matching frames.</param>
    /// <returns>Callback ID string.</returns>
    public string OnMessage(object id, Action<CanFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var callbackId = $"msg_{Interlocked.Increment(ref _callbackCounter)}";

        if (id is int intId)
        {
            // Exact CAN ID match.
            var bucket = _messageCallbacks.GetOrAdd((uint)intId, _ => new());
            bucket[callbackId] = callback;
            LogMessageCallbackRegistered(_logger, callbackId, intId);
        }
        else if (id is string prefix)
        {
            // Hex prefix match.
            var bucket = _prefixCallbacks.GetOrAdd(prefix.ToUpper(CultureInfo.InvariantCulture), _ => new());
            bucket[callbackId] = callback;
            LogPrefixCallbackRegistered(_logger, callbackId, prefix);
        }
        else
        {
            throw new ArgumentException("id must be a number (exact CAN ID) or string (hex prefix)", nameof(id));
        }

        return callbackId;
    }

    /// <summary>
    /// Unregister a message callback.
    /// </summary>
    /// <param name="id">Same ID used in <see cref="OnMessage"/>.</param>
    /// <param name="callbackId">Callback ID returned by <see cref="OnMessage"/>.</param>
    public void OffMessage(object id, string callbackId)
    {
        if (id is int intId)
        {
            if (_messageCallbacks.TryGetValue((uint)intId, out var bucket))
            {
                bucket.TryRemove(callbackId, out _);
            }
        }
        else if (id is string prefix)
        {
            if (_prefixCallbacks.TryGetValue(prefix.ToUpper(CultureInfo.InvariantCulture), out var bucket))
            {
                bucket.TryRemove(callbackId, out _);
            }
        }
    }

    /// <summary>
    /// Check if a CAN channel is currently connected.
    /// </summary>
    public bool IsConnected() => _sendService.ActiveChannel is { IsConnected: true };

    /// <summary>
    /// Get the channel ID string (e.g., "PCAN_USBBUS1").
    /// Returns null if no channel is connected.
    /// </summary>
    public string? GetChannelId() => _sendService.ActiveChannel?.Id.Handle.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Called by ChannelRouter when a frame is received.
    /// Dispatches to registered callbacks.
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        // Dispatch to all-frame callbacks.
        foreach (var callback in _frameCallbacks.Values)
        {
            try
            {
                callback(frame);
            }
            catch (Exception ex)
            {
                LogFrameCallbackError(_logger, ex);
            }
        }

        // Dispatch to exact-ID callbacks.
        // Script authors register with a plain int literal (e.g.
        // can.onMessage(0x100, cb) for an Extended frame with on-wire
        // ID 0x100), so the dictionary key is the raw 11/29-bit ID —
        // intentionally NOT the merged-IDE form used by
        // DbcDocument.MessagesById (see DbcDecodeBackgroundService.cs
        // for the contrast). Lookup uses frame.Id.Raw to match the
        // registration convention; do not "consistency-fix" this to
        // OR with 0x80000000u.
        if (_messageCallbacks.TryGetValue(frame.Id.Raw, out var idCallbacks))
        {
            foreach (var callback in idCallbacks.Values)
            {
                try
                {
                    callback(frame);
                }
                catch (Exception ex)
                {
                    LogMessageCallbackError(_logger, ex, frame.Id.Raw);
                }
            }
        }

        // Dispatch to prefix callbacks.
        var hexId = frame.Id.Raw.ToString("X", CultureInfo.InvariantCulture);
        foreach (var (prefix, callbacks) in _prefixCallbacks)
        {
            if (hexId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var callback in callbacks.Values)
                {
                    try
                    {
                        callback(frame);
                    }
                    catch (Exception ex)
                    {
                        LogPrefixCallbackError(_logger, ex, prefix);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Called by ChannelRouter when a sink error occurs.
    /// </summary>
    public void OnError(Exception ex)
    {
        LogSinkError(_logger, ex);
    }

    /// <summary>
    /// Cleanup: unsubscribe from ChannelRouter.
    /// </summary>
    public void Dispose()
    {
        _channelRouter.DetachSink(this);
        _frameCallbacks.Clear();
        _messageCallbacks.Clear();
        _prefixCallbacks.Clear();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid CAN ID: 0x{Id:X}")]
    private static partial void LogInvalidCanId(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send called with null or empty data")]
    private static partial void LogSendEmptyData(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Data length {Length} exceeds max DLC {MaxDlc} for {FrameType} frame")]
    private static partial void LogDataTooLong(ILogger logger, int length, int maxDlc, string frameType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Frame send failed: {Error}")]
    private static partial void LogSendFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Registered frame callback {CallbackId}")]
    private static partial void LogFrameCallbackRegistered(ILogger logger, string callbackId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Unregistered frame callback {CallbackId}")]
    private static partial void LogFrameCallbackUnregistered(ILogger logger, string callbackId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Registered message callback {CallbackId} for ID 0x{Id:X}")]
    private static partial void LogMessageCallbackRegistered(ILogger logger, string callbackId, int id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Registered message callback {CallbackId} for prefix '{Prefix}'")]
    private static partial void LogPrefixCallbackRegistered(ILogger logger, string callbackId, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Frame callback threw an exception")]
    private static partial void LogFrameCallbackError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message callback threw an exception for ID 0x{Id:X}")]
    private static partial void LogMessageCallbackError(ILogger logger, Exception ex, uint id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix callback threw an exception for prefix '{Prefix}'")]
    private static partial void LogPrefixCallbackError(ILogger logger, Exception ex, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CanApi sink error")]
    private static partial void LogSinkError(ILogger logger, Exception ex);
}
