// CanApi/SendAndQuery.partial.cs — W26 T3 (Flow C, ~45 LoC)
// IScriptCanApi send + query surface: Send() encodes a CAN frame
// via DbcEncodeService-style helpers (CanId + CanFrame constructors)
// + IsConnected() + GetChannelId(). 3 methods total.
//
// Touches _sendService + _logger. Calls 4 [LoggerMessage] partials
// (LogInvalidCanId + LogSendEmptyData + LogDataTooLong + LogSendFailed)
// which all stay on CanApi.cs per W18 R1 + W22 D4 + W23 D4 + W25 D4
// sister precedent (CS8795 mitigation).
//
// W26 T3 verbatim re-extracted via 3 non-contiguous ranges:
// - Send (L56-L98 with xmldoc)
// - IsConnected (L110 single-line)
// - GetChannelId (L148 single-line)
// per W20 T2 R1 fabrication LESSON (26th application) +
// W23 STRUCT-FABRICATION LESSON (6th since 3/3 CONFIRMED at W23):
// verified CanFrame(canId, payload, FrameFlags, ChannelId, default)
// 5-arg ctor + CanId(raw, FrameFormat format) 2-arg ctor + FrameFormat
// enum + FrameFlags enum + ChannelId.None (default).

using System.Globalization;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class CanApi
{
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
    /// Check if a CAN channel is currently connected.
    /// </summary>
    public bool IsConnected() => _sendService.ActiveChannel is { IsConnected: true };

    /// <summary>
    /// Get the channel ID string (e.g., "PCAN_USBBUS1").
    /// Returns null if no channel is connected.
    /// </summary>
    public string? GetChannelId() => _sendService.ActiveChannel?.Id.Handle.ToString("X2", CultureInfo.InvariantCulture);
}
