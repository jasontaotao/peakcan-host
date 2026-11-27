namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// 一个请求的响应「校验到什么程度」。
/// <para>
/// <b>为什么必须区分：</b>spec §8.5 要求「不在表中的服务只做 SID 配对与最小长度
/// （≥ 2）；无法校验的部分<b>明确记为「未校验」，不冒充已校验</b>」。
/// 报告里 <c>0x22</c>（真的比对了 DID 回显）和 <c>0x19</c>（只看了长度）
/// 长得一样时，用户会把「长度对上了」当成「响应是对的」。
/// </para>
/// </summary>
public enum UdsEchoCheckLevel
{
    /// <summary>校验了回显字段（DID / 子功能 / 例程 ID / 块序号 / 会话类型）。</summary>
    FullEcho,

    /// <summary>
    /// 只校验了长度，内容未比对——报告必须显式标注。
    /// <para>
    /// 两种情形：① 服务在 §8.5 表内但<b>没有可回显字段</b>（<c>0x34</c> 的
    /// lengthFormatIdentifier 首版不展开、<c>0x37</c> 合法响应只有 1 字节）；
    /// ② 服务<b>根本不在表内</b>（<c>0x19</c> / <c>0x28</c> / <c>0x85</c> / OEM 自定义），
    /// 只做了「SID 配对 + 最小长度 ≥ 2」。
    /// </para>
    /// </summary>
    LengthOnly,

    /// <summary>
    /// 没校验过。负响应（只验 3 字节长度）和未闭合事务（根本没有最终响应）
    /// 属于这一类——它们不能借用请求 SID 的校验强度。
    /// </summary>
    NotChecked,
}

/// <summary>
/// Offline echo checks: a positive response must carry the request's own
/// identifiers back (DID, sub-function, routine id, block sequence). Without
/// this, a periodic frame that happens to look like <c>0x76</c> on the same CAN
/// id silently becomes a successful transfer — the exact failure mode this
/// project exists to prevent.
/// </summary>
internal static class UdsResponseValidator
{
    /// <summary>
    /// 该服务的正响应校验强度。表外的服务返回 <see cref="UdsEchoCheckLevel.LengthOnly"/>，
    /// 报告据此显示「仅长度校验」而不是让它看起来和全回显校验一样。
    /// <para>
    /// 刻意做成纯查表、不改 <see cref="TryValidate"/> 的返回值：改成三态会波及
    /// 所有调用点，而这里的语义是「报告层如何陈述校验强度」，与配对判定无关。
    /// </para>
    /// </summary>
    public static UdsEchoCheckLevel EchoCheckLevelFor(byte sid) => sid switch
    {
        0x10 or 0x11 or 0x19 or 0x22 or 0x28 or 0x2E or 0x27 or 0x31 or 0x36 or 0x3E or 0x85
            => UdsEchoCheckLevel.FullEcho,
        _ => UdsEchoCheckLevel.LengthOnly,
    };

    public static bool TryValidate(UdsOfflinePdu request, UdsOfflinePdu response, out string? failed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        failed = null;
        var req = request.Payload;
        var res = response.Payload;

        // 只校验「请求非空 + 响应非空」。响应最小长度必须逐服务判断：
        // 0x37 RequestTransferExit 的合法正响应是单字节 0x77，用统一的
        // `res.Length < 2` 会把它误判成 PayloadTooShort——于是真实刷写日志的
        // 0x37 永远不闭合事务，刷写被显示为「未完成」。
        if (req.Length == 0 || res.Length == 0) return Fail(out failed, "PayloadTooShort");

        switch (req[0])
        {
            case 0x10: // Diagnostic Session Control
                if (req.Length < 2) return Fail(out failed, "RequestTooShort");
                if (res.Length < 6) return Fail(out failed, "PayloadTooShort");
                // 回显必须与子功能的低 7 位比：请求 10 83（抑制位）的合法回显是
                // 50 03，拿原始字节比会把合法响应判成 SessionTypeEcho 失败，
                // 事务因此判未闭合，再被归咎「ECU 没响应」。
                return res[1] == (req[1] & UdsSubFunction.SubFunctionMask)
                    || Fail(out failed, "SessionTypeEcho");

            case 0x22: // Read Data By Identifier
            case 0x2E: // Write Data By Identifier
                if (req.Length < 3) return Fail(out failed, "RequestTooShort");
                if (res.Length < 3) return Fail(out failed, "PayloadTooShort");
                return (res[1] == req[1] && res[2] == req[2]) || Fail(out failed, "DidEcho");

            case 0x11: // ECU Reset — echoes the reset type, low 7 bits
            case 0x19: // Read DTC Information — echoes the report type, low 7 bits
            case 0x27: // Security Access — echoes the sub-function without the suppress bit
            case 0x28: // Communication Control — echoes the control type
            case 0x3E: // Tester Present
            case 0x85: // Control DTC Setting — echoes the DTC setting type
                if (req.Length < 2) return Fail(out failed, "RequestTooShort");
                if (res.Length < 2) return Fail(out failed, "PayloadTooShort");
                return res[1] == (req[1] & UdsSubFunction.SubFunctionMask)
                    || Fail(out failed, "SubFunctionEcho");

            case 0x31: // Routine Control
                if (req.Length < 4) return Fail(out failed, "RequestTooShort");
                if (res.Length < 4) return Fail(out failed, "PayloadTooShort");
                // 子功能（routineControlType）同样要去掉抑制位再比。
                return (res[1] == (req[1] & UdsSubFunction.SubFunctionMask)
                        && res[2] == req[2] && res[3] == req[3])
                    || Fail(out failed, "RoutineIdEcho");

            case 0x34: // Request Download — maxNumberOfBlockLength present
                return res.Length >= 3 || Fail(out failed, "PayloadTooShort");

            case 0x36: // Transfer Data — echo the block sequence counter
                if (req.Length < 2) return Fail(out failed, "RequestTooShort");
                if (res.Length < 2) return Fail(out failed, "PayloadTooShort");
                return res[1] == req[1] || Fail(out failed, "BlockSequenceEcho");

            // 以下服务的正响应合法地只有 1 字节，必须逐个显式列出。
            // 靠 default 兜会让「单字节响应一律接受」，等于把未知服务的最小长度
            // 保护一起放弃——一个长得像 0x59 的周期帧就能闭合事务。
            case 0x37: // Request Transfer Exit → 0x77
            case 0x14: // ClearDiagnosticInformation → 0x54
            case 0x3D: // WriteMemoryByAddress → 0x7D
                return true;

            default:
                // 未列服务：正响应至少要有 SID + 子功能/标识符。
                return res.Length >= 2 || Fail(out failed, "PayloadTooShort");
        }
    }

    private static bool Fail(out string? failed, string reason)
    {
        failed = reason;
        return false;
    }
}
