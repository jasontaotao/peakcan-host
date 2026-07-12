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

    /// <summary>
    /// Check if a CAN channel is currently connected.
    /// </summary>
    public bool IsConnected() => _sendService.ActiveChannel is { IsConnected: true };

    /// <summary>
    /// v1.7.1 PATCH Item 1: explicit interface implementation of
    /// <see cref="IScriptCanApi.IsConnected"/> property. Forwards to
    /// the existing <see cref="IsConnected"/> method. CanApi's public
    /// method-based API is preserved for non-script consumers; only
    /// scripts (which see CanApi through the IScriptCanApi interface)
    /// access the property form.
    /// </summary>
    bool IScriptCanApi.IsConnected => IsConnected();

    /// <summary>
    /// v1.7.1 PATCH Item 1: explicit interface implementation of
    /// <see cref="IScriptCanApi.Send(CanFrame)"/> overload. Extracts
    /// <c>Id.Raw</c>, <c>Data</c>, and <c>Flags</c> from the frame and
    /// delegates to the existing <see cref="Send(int, byte[], bool, bool)"/>
    /// method. Additive convenience for decode-then-resend script patterns.
    /// <para>
    /// <c>CanFrame.Data</c> is <see cref="ReadOnlyMemory{T}"/>; the
    /// underlying byte array is extracted via <c>ToArray()</c> to match
    /// the public <c>Send(int, byte[], ...)</c> signature. CanFrame is a
    /// value type (record struct) so no null check is needed.
    /// </para>
    /// </summary>
    async Task<bool> IScriptCanApi.Send(CanFrame frame)
    {
        return await Send(
            (int)frame.Id.Raw,
            frame.Data.ToArray(),
            fd: (frame.Flags & FrameFlags.Fd) != 0,
            extended: frame.Id.Format == FrameFormat.Extended).ConfigureAwait(false);
    }

    /// <summary>
    /// Get the channel ID string (e.g., "PCAN_USBBUS1").
    /// Returns null if no channel is connected.
    /// </summary>
    public string? GetChannelId() => _sendService.ActiveChannel?.Id.Handle.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Called by ChannelRouter when a frame is received.
    /// Dispatches to registered callbacks.
    /// </summary>

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
