namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Offline echo checks: a positive response must carry the request's own
/// identifiers back (DID, sub-function, routine id, block sequence). Without
/// this, a periodic frame that happens to look like <c>0x76</c> on the same CAN
/// id silently becomes a successful transfer — the exact failure mode this
/// project exists to prevent.
/// </summary>
internal static class UdsResponseValidator
{
    public static bool TryValidate(UdsOfflinePdu request, UdsOfflinePdu response, out string? failed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        failed = null;
        var req = request.Payload;
        var res = response.Payload;

        if (req.Length == 0 || res.Length < 2) return Fail(out failed, "PayloadTooShort");

        switch (req[0])
        {
            case 0x10: // Diagnostic Session Control
                if (res.Length < 6) return Fail(out failed, "PayloadTooShort");
                return res[1] == req[1] || Fail(out failed, "SessionTypeEcho");

            case 0x22: // Read Data By Identifier
            case 0x2E: // Write Data By Identifier
                if (req.Length < 3) return Fail(out failed, "RequestTooShort");
                if (res.Length < 3) return Fail(out failed, "PayloadTooShort");
                return (res[1] == req[1] && res[2] == req[2]) || Fail(out failed, "DidEcho");

            case 0x27: // Security Access — response echoes the sub-function without the suppress bit
            case 0x3E: // Tester Present
                if (req.Length < 2) return Fail(out failed, "RequestTooShort");
                return res[1] == (req[1] & 0x7F) || Fail(out failed, "SubFunctionEcho");

            case 0x31: // Routine Control
                if (req.Length < 4) return Fail(out failed, "RequestTooShort");
                if (res.Length < 4) return Fail(out failed, "PayloadTooShort");
                return (res[2] == req[2] && res[3] == req[3]) || Fail(out failed, "RoutineIdEcho");

            case 0x34: // Request Download — maxNumberOfBlockLength present
                return res.Length >= 3 || Fail(out failed, "PayloadTooShort");

            case 0x36: // Transfer Data — echo the block sequence counter
                if (req.Length < 2) return Fail(out failed, "RequestTooShort");
                return res[1] == req[1] || Fail(out failed, "BlockSequenceEcho");

            case 0x37: // Request Transfer Exit
                return res.Length >= 1 || Fail(out failed, "PayloadTooShort");

            default:
                // 未列服务：SID 配对 + 最小长度检查已在上面完成。
                return true;
        }
    }

    private static bool Fail(out string? failed, string reason)
    {
        failed = reason;
        return false;
    }
}
