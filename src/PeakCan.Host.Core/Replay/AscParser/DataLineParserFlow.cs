using System.Globalization;

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow A: DataLineParser (v1.4.0 MINOR + v3.11.5 PATCH + earlier).
    // TryParseDataLine: vector-ASC data line parser. 1-char single-hex +
    // N*N 2-char hex + odd-length malformed classification + v3.11.5 PATCH
    // ID+0x extension marker stripping + Rx/Tx direction pre-scan + 'l' CAN-FD
    // DLC marker + v3.11.5 PATCH trailing-metadata markers (Length/BitCount/ID/=)
    // + DLC invariant check.
    //
    // Cross-flow callers (partial-class visible):
    //   - TryParseDataLine <- ParseLines (Flow B)

    private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)
    {
        frame = default!;
        reason = string.Empty;
        // Format: {timestamp:F6} {channel:X2}  {id:X}  {dlc}  {dataBytes...} {flags...}
        // Data bytes may be either space-separated (Vector ASC convention) or
        // concatenated (RecordService's Convert.ToHexString output). Each post-DLC
        // token is classified as either a flag keyword or a hex byte (or
        // concatenation of 2-char hex bytes).
        var tokens = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4)
        {
            reason = $"expected >=4 tokens (timestamp channel id dlc), got {tokens.Length}";
            return false;
        }

        if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
        {
            reason = $"invalid timestamp '{tokens[0]}'";
            return false;
        }

        // v3.11.5 PATCH: Vector convention appends 'x' to the hex ID for
        // extended frames. Strip before hex parse so '18FF60A2x' -> 0x18FF60A2.
        var idToken = tokens[2];
        if (idToken.EndsWith("x", StringComparison.OrdinalIgnoreCase))
        {
            idToken = idToken.Substring(0, idToken.Length - 1);
        }
        if (!uint.TryParse(idToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            reason = $"invalid CAN id '{tokens[2]}'";
            return false;
        }

        // v3.11.5 PATCH: Vector convention may insert an optional 'Rx'/'Tx'
        // direction token before the 'd N' / 'l N' DLC marker. Scan forward
        // from tokens[3] until we find the dlc marker, skipping direction
        // tokens. The 'l' form implies FrameFlags.Fd.
        int dataStartIndex = 4;
        byte dlc;
        FrameFlags flags = FrameFlags.None;
        int scan = 3;
        // Skip optional Rx/Tx direction tokens
        while (scan < tokens.Length &&
               (tokens[scan].Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                tokens[scan].Equals("tx", StringComparison.OrdinalIgnoreCase)))
        {
            scan++;
        }
        if (scan + 1 < tokens.Length &&
            (tokens[scan].Equals("d", StringComparison.OrdinalIgnoreCase) ||
             tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase)))
        {
            if (!byte.TryParse(tokens[scan + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
            {
                reason = $"invalid DLC after Vector 'd/l' marker '{tokens[scan + 1]}'";
                return false;
            }
            if (tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase))
            {
                flags |= FrameFlags.Fd;  // 'l' = CAN FD
            }
            dataStartIndex = scan + 2;
        }
        else if (!byte.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
        {
            reason = $"invalid DLC '{tokens[3]}'";
            return false;
        }

        var data = new List<byte>(capacity: Math.Max((int)dlc, 8));
        for (int i = dataStartIndex; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.ToLowerInvariant())
            {
                case "fd": flags |= FrameFlags.Fd; continue;
                case "brs": flags |= FrameFlags.BitRateSwitch; continue;
                case "esi": flags |= FrameFlags.ErrorStateIndicator; continue;
                case "error": flags |= FrameFlags.ErrFrame; continue;
                // v3.11.5 PATCH: Vector ASC v1.3 direction tokens. Direction tracking
                // is not surfaced in ReplayFrame (future PATCH); for now classify as
                // flags so they don't get parsed as data bytes.
                case "rx": continue;
                case "tx": continue;
                // v3.11.5 PATCH: Vector ASC v1.3 trailing-metadata markers. The
                // "Length = N BitCount = N ID = Nx" tail is appended after the
                // data bytes. Treat the first non-hex token (containing '=' OR
                // matching a known metadata keyword like Length/BitCount/ID) as
                // the start of the metadata tail and stop reading data bytes.
                default:
                    if (t.Contains('=') ||
                        t.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("BitCount", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        goto EndDataBytes;
                    }
                    break;
            }
            // Not a flag -> must be hex bytes. Tokens may be either:
            // - Space-separated 1-2 char hex bytes (Vector ASC convention)
            // - Concatenated 2*N chars (RecordService uses Convert.ToHexString,
            //   producing e.g. "1122334455667788" for DLC=8)
            // Slice longer tokens into 2-char chunks; odd-length tokens are
            // malformed (return false).
            if (t.Length == 0)
            {
                reason = "empty data token";
                return false;
            }
            if (t.Length == 1)
            {
                if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    reason = $"invalid single-hex byte '{t}'";
                    return false;
                }
                data.Add(b);
            }
            else if (t.Length % 2 != 0)
            {
                // Odd length: ambiguous, treat as malformed
                reason = $"odd-length hex token '{t}' (length {t.Length})";
                return false;
            }
            else
            {
                // Even length 2+: slice into 2-char hex bytes
                for (int j = 0; j < t.Length; j += 2)
                {
                    if (!byte.TryParse(t.AsSpan(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        reason = $"invalid hex pair in '{t}' at offset {j}";
                        return false;
                    }
                    data.Add(b);
                }
            }
        }
        EndDataBytes:;

        // Invariant: byte count must match declared DLC. Truncated user-imported
        // ASC lines (e.g. DLC=8 but only 2 bytes) are treated as malformed and
        // skipped per spec Decision 3 (logged + skipped, not thrown).
        if (data.Count != dlc)
        {
            reason = $"byte count {data.Count} != declared DLC {dlc}";
            return false;
        }

        frame = new ReplayFrame(ts, id, dlc, data.ToArray(), flags);
        return true;
    }
}
