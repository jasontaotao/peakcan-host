using System.Globalization;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.4.2 PATCH: parses the global Trace Viewer "Filter CAN IDs" string
/// into a set of allowed CAN IDs. Comma-separated, decimal or 0x-hex,
/// case-insensitive. Empty / unparseable → null (no filter).
/// </summary>
public static class CanIdFilterParser
{
    /// <summary>
    /// Returns null when the input is empty / whitespace / contains no
    /// valid IDs (caller treats null as "no filter"). Invalid entries are
    /// silently skipped — UX is "type junk and see nothing match" rather
    /// than "type junk and see a popup".
    /// </summary>
    public static IReadOnlySet<uint>? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var set = new HashSet<uint>();
        foreach (var raw in input.Split(','))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            // Detect 0x/0X prefix and strip it before parsing as hex.
            // NumberStyles.AllowHexSpecifier alone treats leading "0x" as
            // literal digits, NOT a hex prefix; trimming it here lets
            // the standard hex path parse the digits cleanly.
            bool isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            var digits = isHex ? trimmed[2..] : trimmed;
            if (digits.Length == 0) continue;
            var style = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
            if (uint.TryParse(digits, style, CultureInfo.InvariantCulture, out var id))
                set.Add(id);
            // else: silently skip — see xmldoc above
        }
        return set.Count == 0 ? null : set;
    }
}