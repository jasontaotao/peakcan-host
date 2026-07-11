using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SendViewModel
{
    // Flow A: FrameSend (v1.2.11 PATCH Item 4 + earlier).
    // Methods + log helpers moved verbatim from SendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - SendAsync -> _svc (DI, main) + ParseHex + BuildFlags helpers (main)
    //   - SendAsync -> Cyclic state (Flow B) — manual send does NOT stop cyclic
    //
    // [RelayCommand] attribute MUST travel with SendAsync.

    /// <summary>
    /// Raised by <see cref="OnRateLimitRejectedCountChanged"/> whenever
    /// <see cref="RateLimitRejectedCount"/> changes so WPF re-evaluates
    /// <see cref="RateLimitRejectedVisibility"/>. Without this hook the
    /// Visibility binding would not refresh because the underlying
    /// property change notification is for the long, not the computed
    /// Visibility.
    /// </summary>
    partial void OnRateLimitRejectedCountChanged(long value)
        => OnPropertyChanged(nameof(RateLimitRejectedVisibility));

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!uint.TryParse(IdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            Status = $"Invalid ID: {IdText}";
            LogInvalidId(_logger, IdText);
            return;
        }
        // Pre-validate ID against the chosen frame format. CanId ctor
        // throws ArgumentOutOfRangeException for out-of-range raw values;
        // we surface a friendly status here instead of letting the
        // SDK exception path handle it.
        var maxId = IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (raw > maxId)
        {
            Status = $"ID 0x{raw:X} exceeds max for {(IsExtended ? "Extended (29-bit)" : "Standard (11-bit)")} (max 0x{maxId:X})";
            LogInvalidId(_logger, IdText);
            return;
        }
        byte[] bytes;
        try
        {
            bytes = ParseHex(DataText);
        }
        catch (FormatException ex)
        {
            // Defensive: ParseHex only throws on a non-hex character that
            // survived the strip-separator step, OR on an all-separator
            // input that strips down to empty (footgun: user clicks Send
            // after clearing the data field). Treat both as a user input
            // error, not a bug.
            Status = $"Invalid data: {ex.Message}";
            LogInvalidData(_logger, DataText, ex);
            return;
        }
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        // v1.2.11 PATCH Item 4: RTR + FD is not a valid CAN frame per the
        // ISO 11898-1 spec (RTR applies to classic CAN only). Reject loudly
        // so the user fixes the input rather than seeing a silent zero-byte
        // classic frame go out.
        if (IsRtr && IsFd)
        {
            Status = "RTR is not valid for CAN FD (classic CAN only)";
            LogInvalidId(_logger, "RTR+FD");
            return;
        }
        var flags = BuildFlags();
        var frame = new CanFrame(canId, bytes, flags, ChannelId.None, default);
        try
        {
            var r = await _svc.SendAsync(frame).ConfigureAwait(true);
            Status = r.IsSuccess
                ? $"Sent {bytes.Length} bytes @ 0x{canId}"
                : $"FAIL: {r.Error!.Code} {r.Error.Message}";
            if (r.IsSuccess)
            {
                LogSendOk(_logger, canId, bytes.Length);
            }
            else
            {
                LogSendFailed(_logger, canId, r.Error!.Code, r.Error.Message);
            }
        }
        catch (Exception ex)
        {
            // Never let an SDK / channel exception escape the command —
            // the WPF dispatcher would surface it as an unhandled
            // exception and crash the app on a non-issue.
            Status = $"FAIL: {ex.Message}";
            LogSendThrew(_logger, canId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send rejected: invalid ID hex '{Input}'")]
    private static partial void LogInvalidId(ILogger logger, string input);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send rejected: invalid data hex '{Input}'")]
    private static partial void LogInvalidData(ILogger logger, string input, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent CAN frame {CanId} ({Length} bytes)")]
    private static partial void LogSendOk(ILogger logger, CanId canId, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send failed for {CanId}: {Code} {Message}")]
    private static partial void LogSendFailed(ILogger logger, CanId canId, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Send threw for {CanId}")]
    private static partial void LogSendThrew(ILogger logger, CanId canId, Exception ex);
}