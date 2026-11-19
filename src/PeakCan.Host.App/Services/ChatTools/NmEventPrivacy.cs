using System.Text.RegularExpressions;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Payload redaction for NM event details sent to the LLM.
/// <para>
/// Content events (<c>byte_change</c> / <c>signal_change</c>) embed raw bus
/// values. The chat boundary only forwards structure (byte/bit indices,
/// change counts, time ranges, DBC signal names) per the privacy default.
/// The panel keeps the unredacted <see cref="PeakCan.Host.Core.Analysis.Nm.NmEvent.Detail"/>;
/// only tool output is redacted.
/// </para>
/// </summary>
internal static partial class NmEventPrivacy
{
    private const string RedactedHexValue = "0x??";

    /// <summary>Returns a detail string safe to send to the LLM. Event types
    /// without payload values pass through unchanged.</summary>
    internal static string RedactDetail(string type, string detail) => type switch
    {
        // "byte0: 0x00 → 0x08，变化 N 次（..." → "byte0: 0x?? → 0x??，变化 N 次（..."
        "byte_change" or "bit_change" => HexValueRegex().Replace(detail, RedactedHexValue),
        // "WakeReason: 85 → 2" → "WakeReason: 变化"
        "signal_change" => RedactSignalChange(detail),
        // burst_observed / all_nm_silent / sender_silent / bus_wide_silence
        // 只含时间、时长、帧数，无需脱敏。
        _ => detail,
    };

    private static string RedactSignalChange(string detail)
    {
        var colon = detail.IndexOf(':');
        return colon < 0 ? detail : detail[..colon] + ": 变化";
    }

    [GeneratedRegex("0x[0-9A-Fa-f]+")]
    private static partial Regex HexValueRegex();
}
