using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// View-model for the manual-send form (<c>SendView.xaml</c>). Two text
/// inputs (ID + Data) and two checkboxes (Extended / CAN FD) feed a single
/// <see cref="RelayCommand"/> that builds a <see cref="CanFrame"/>, hands
/// it to <see cref="SendService"/>, and surfaces the outcome in
/// <see cref="Status"/>.
/// <para>
/// The command is intentionally <c>async</c> because
/// <see cref="SendService.SendAsync"/> awaits the PEAK SDK on a worker
/// thread; exceptions from that path are caught and turned into a
/// "FAIL: …" status rather than propagating out of the command (which
/// would surface as an unhandled exception in the WPF dispatcher).
/// </para>
/// </summary>
public sealed partial class SendViewModel : ObservableObject
{
    private readonly SendService _svc;
    private readonly ILogger<SendViewModel> _logger;

    [ObservableProperty]
    private string _idText = "100";

    [ObservableProperty]
    private bool _isExtended;

    [ObservableProperty]
    private bool _isFd;

    [ObservableProperty]
    private string _dataText = "DEADBEEF";

    [ObservableProperty]
    private string _status = string.Empty;

    public SendViewModel(SendService svc, ILogger<SendViewModel> logger)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
        var flags = IsFd ? FrameFlags.Fd : FrameFlags.None;
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

    /// <summary>
    /// Parse a hex string into bytes. Accepts optional spaces and dashes
    /// as separators (e.g. <c>"DE AD-BE EF"</c>) and pads odd-length input
    /// with a leading zero (e.g. <c>"ABC"</c> → <c>{0x0A, 0xBC}</c>).
    /// </summary>
    /// <exception cref="FormatException">A non-hex character survived the separator strip, OR the input was empty / separators-only (no hex digits to parse).</exception>
    private static byte[] ParseHex(string s)
    {
        var stripped = s.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("-", string.Empty, StringComparison.Ordinal);
        if (stripped.Length == 0)
        {
            // Footgun guard: clicking Send with an empty Data field (or
            // spaces/dashes only) previously produced a silent DLC=0
            // transmission. Reject loudly so the user fixes the input.
            throw new FormatException("Hex data is empty (only separators or no input).");
        }
        if ((stripped.Length & 1) == 1)
        {
            stripped = "0" + stripped;
        }
        var bytes = new byte[stripped.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(stripped.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return bytes;
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
