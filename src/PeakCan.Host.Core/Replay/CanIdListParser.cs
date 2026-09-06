using System.Globalization;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.4.4 PATCH: shared CAN-ID allow-list parser for the Trace Viewer
/// (v3.4.2 foundation) and the Replay tab (v1.5.0 MINOR Task 5).
/// Replaces the v3.4.2 <c>CanIdFilterParser</c> + the inline parser
/// in <c>ReplayViewModel.OnCanIdFilterTextChanged</c>; both callers
/// now delegate here. See <c>CanIdParseResult</c> for the structured
/// tri-state return shape.
/// </summary>
public static class CanIdListParser
{
    private static readonly char[] Separators = { ',', ' ', '\t', '\n', '\r' };

    public static CanIdParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return CanIdParseResult.Empty;

        var set = new HashSet<uint>();
        var invalid = new List<string>();
        foreach (var raw in input.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            // Same manual 0x strip as v3.4.2 — see allowhexspecifier-rejects-0x-prefix.
            bool isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            var digits = isHex ? trimmed[2..] : trimmed;
            if (digits.Length == 0)
            {
                invalid.Add(trimmed);
                continue;
            }
            var style = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
            if (uint.TryParse(digits, style, CultureInfo.InvariantCulture, out var id))
                set.Add(id);
            else
                invalid.Add(trimmed);
        }

        // Tri-state: null = no filter, empty set = all-invalid, populated = whitelist.
        var result = set.Count > 0
            ? (IReadOnlySet<uint>?)set
            : (invalid.Count > 0 ? new HashSet<uint>() : null);
        return new CanIdParseResult(result, invalid);
    }
}

/// <summary>
/// v3.4.4 PATCH: structured return for <see cref="CanIdListParser.Parse"/>.
/// <c>AllowList</c> is null = "no filter" (input was empty/whitespace-only),
/// empty set = "all tokens invalid" (Replay emits nothing), populated =
/// whitelist.
/// <c>InvalidTokens</c> lists the tokens that failed to parse — callers
/// may surface them via UI (Replay) or ignore them (Trace Viewer).
/// </summary>
public readonly record struct CanIdParseResult(
    IReadOnlySet<uint>? AllowList,
    IReadOnlyList<string> InvalidTokens)
{
    public static readonly CanIdParseResult Empty = new(null, Array.Empty<string>());
    public bool HasInvalidTokens => InvalidTokens.Count > 0;
}