namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>会话状态是怎么确定的——观测等级，不是会话类型。</summary>
public enum UdsSessionObservation
{
    /// <summary>收到与请求匹配的 <c>0x50</c> 正响应，会话已确认。</summary>
    Confirmed,

    /// <summary>发了 <c>0x10</c>，但没有通过校验的响应。ECU 会话状态未知。</summary>
    RequestedNotConfirmed,

    /// <summary>之后出现 <c>0x11</c> ECUReset，会话可能已被硬件复位改变。</summary>
    ResetMayHaveChanged,

    /// <summary>
    /// 日志开始时 ECU 已处于某个会话，但日志里没有对应的 <c>0x10</c> 可查。
    /// <b>不能默认成「默认会话」</b>——那是编造事实。
    /// </summary>
    UnknownAtLogStart,

    /// <summary>
    /// 发了 <c>0x10</c> 且声明了抑制正响应（如 <c>10 83</c>），ECU 按协议不回。
    /// <para>
    /// <b>会话「很可能已切换」，但没有证据。</b>既不能说「已确认」（没收到响应），
    /// 也不能说「请求未确认」（那暗示 ECU 有问题）。时间线要按请求的会话类型开区间，
    /// 否则后续所有「这条请求在哪个会话下发的」都会答成切换前的旧会话。
    /// </para>
    /// </summary>
    RequestedResponseSuppressed,
}

/// <summary>
/// 一段会话区间。<see cref="SessionType"/> 为 null 表示「不知道是哪个会话」，
/// 与「知道是默认会话」是两回事。
/// </summary>
public sealed record UdsSessionInterval(
    byte? SessionType,
    UdsSessionObservation Observation,
    double StartTimestamp,
    int StartEvidenceIndex,
    double? EndTimestamp,
    int? EndEvidenceIndex);

/// <summary>
/// 从已配对的事务里还原会话时间线。
/// <para>
/// 只消费 <c>0x10 DiagnosticSessionControl</c> 与 <c>0x11 ECUReset</c> 两类事务。
/// 判据刻意保守：<b>没有 <c>0x50</c> 正响应就不推进会话状态</b>，宁可显示
/// 「请求未确认」也不宣称 ECU 已经切换。
/// </para>
/// <para>
/// 首项恒为 <see cref="UdsSessionObservation.UnknownAtLogStart"/>（除非日志正好
/// 从一次成功的会话切换开始）：诊断日志通常从会话中途开始，把起点假定成
/// 默认会话会让后续所有「这条请求在哪个会话下发的」判断都建立在编造的前提上。
/// </para>
/// </summary>
public sealed class UdsSessionStateTracker
{
    private const byte SessionControlSid = 0x10;
    private const byte SessionControlPositiveSid = 0x50;
    private const byte EcuResetSid = 0x11;
    private const byte EcuResetPositiveSid = 0x51;

    public IReadOnlyList<UdsSessionInterval> Track(IReadOnlyList<UdsOfflineTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        if (transactions.Count == 0) return Array.Empty<UdsSessionInterval>();

        var ordered = transactions.OrderBy(t => t.Request.FirstTimestamp).ToArray();
        var result = new List<UdsSessionInterval>();

        // 日志开头到第一次会话切换之间：会话未知。
        // 若首个事务本身就是成功的 0x10，则不需要这段占位。
        var first = ordered[0];
        if (!IsConfirmedSessionSwitch(first))
        {
            // End 留 null，由后续的 CloseLast 在第一次会话事件处闭合。
            // 若这里就填 End = Start，区间恒为零长——P1 的时间轴查询在
            // (日志开始, 首次切换) 内会找不到任何区间。
            result.Add(new(
                SessionType: null,
                Observation: UdsSessionObservation.UnknownAtLogStart,
                StartTimestamp: first.Request.FirstTimestamp,
                StartEvidenceIndex: first.Request.FirstEvidenceIndex,
                EndTimestamp: null,
                EndEvidenceIndex: null));
        }

        foreach (var tx in ordered)
        {
            if (TryConfirmedSessionSwitch(tx, out var target))
            {
                CloseLast(result, tx.Request.FirstTimestamp, tx.Request.FirstEvidenceIndex);
                result.Add(new(
                    SessionType: target,
                    Observation: UdsSessionObservation.Confirmed,
                    StartTimestamp: tx.Request.FirstTimestamp,
                    StartEvidenceIndex: tx.Request.FirstEvidenceIndex,
                    EndTimestamp: null,
                    EndEvidenceIndex: null));
                continue;
            }

            if (TrySuppressedSessionSwitch(tx, out var suppressedTarget))
            {
                // 抑制了正响应的会话切换：ECU 不回是协议要求，但会话很可能已经变了。
                // 必须开新区间——否则后续请求会被答成切换前的旧会话。
                CloseLast(result, tx.Request.FirstTimestamp, tx.Request.FirstEvidenceIndex);
                result.Add(new(
                    SessionType: suppressedTarget,
                    Observation: UdsSessionObservation.RequestedResponseSuppressed,
                    StartTimestamp: tx.Request.FirstTimestamp,
                    StartEvidenceIndex: tx.Request.FirstEvidenceIndex,
                    EndTimestamp: null,
                    EndEvidenceIndex: null));
                continue;
            }

            if (IsUnconfirmedSessionSwitch(tx))
            {
                CloseLast(result, tx.Request.FirstTimestamp, tx.Request.FirstEvidenceIndex);
                result.Add(new(
                    SessionType: null,
                    Observation: UdsSessionObservation.RequestedNotConfirmed,
                    StartTimestamp: tx.Request.FirstTimestamp,
                    StartEvidenceIndex: tx.Request.FirstEvidenceIndex,
                    EndTimestamp: null,
                    EndEvidenceIndex: null));
                continue;
            }

            if (IsEcuReset(tx))
            {
                CloseLast(result, tx.Request.FirstTimestamp, tx.Request.FirstEvidenceIndex);
                result.Add(new(
                    SessionType: null,
                    Observation: UdsSessionObservation.ResetMayHaveChanged,
                    StartTimestamp: tx.Request.FirstTimestamp,
                    StartEvidenceIndex: tx.Request.FirstEvidenceIndex,
                    EndTimestamp: null,
                    EndEvidenceIndex: null));
            }
        }

        return result;
    }

    private static void CloseLast(
        List<UdsSessionInterval> result, double endTs, int endIndex)
    {
        if (result.Count == 0) return;
        var last = result[^1];
        if (last.EndTimestamp is not null) return;
        result[^1] = last with { EndTimestamp = endTs, EndEvidenceIndex = endIndex };
    }

    private static bool IsConfirmedSessionSwitch(UdsOfflineTransaction tx)
        => TryConfirmedSessionSwitch(tx, out _);

    private static bool TryConfirmedSessionSwitch(UdsOfflineTransaction tx, out byte target)
    {
        target = 0;
        var req = tx.Request.Payload;
        if (req.Length < 2 || req[0] != SessionControlSid) return false;

        // 回显比对一律用子功能低 7 位：请求 10 83 的合法回显是 50 03。
        var requested = (byte)(req[1] & UdsSubFunction.SubFunctionMask);

        // 抑制请求走 TrySuppressedSessionSwitch，这里只管真正收到正响应的情况。
        if (tx.FinalKind != UdsFinalKind.Positive || tx.Responses.Count == 0) return false;

        var res = tx.Responses[^1].Payload;
        if (res.Length < 2 || res[0] != SessionControlPositiveSid) return false;
        if (res[1] != requested) return false; // 回显不一致由校验层处理，这里不推进

        target = requested;
        return true;
    }

    /// <summary>
    /// 抑制了正响应的会话切换（如 <c>10 83</c>）。会话很可能已切换，但没有响应证据。
    /// </summary>
    private static bool TrySuppressedSessionSwitch(UdsOfflineTransaction tx, out byte target)
    {
        target = 0;
        var req = tx.Request.Payload;
        if (req.Length < 2 || req[0] != SessionControlSid) return false;
        if (tx.FinalKind != UdsFinalKind.Suppressed) return false;

        target = (byte)(req[1] & UdsSubFunction.SubFunctionMask);
        return true;
    }

    /// <summary>
    /// 发了会话切换但没拿到确认。<b>抑制响应的请求不算</b>——那是协议要求，
    /// 把它标成「请求未确认」会让用户以为 ECU 出了问题。
    /// </summary>
    private static bool IsUnconfirmedSessionSwitch(UdsOfflineTransaction tx)
    {
        var req = tx.Request.Payload;
        return req.Length >= 2
               && req[0] == SessionControlSid
               && tx.FinalKind is UdsFinalKind.None or UdsFinalKind.Negative;
    }

    private static bool IsEcuReset(UdsOfflineTransaction tx)
    {
        var req = tx.Request.Payload;
        if (req.Length < 2 || req[0] != EcuResetSid) return false;

        // 抑制了正响应的复位：ECU 不回，但复位确实执行了——会话状态照样会变。
        if (tx.FinalKind == UdsFinalKind.Suppressed) return true;

        if (tx.FinalKind != UdsFinalKind.Positive || tx.Responses.Count == 0) return false;

        var res = tx.Responses[^1].Payload;
        return res.Length >= 1 && res[0] == EcuResetPositiveSid;
    }
}
