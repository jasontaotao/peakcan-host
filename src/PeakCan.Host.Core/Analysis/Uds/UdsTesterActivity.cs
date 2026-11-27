namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// 日志里「测试仪（tester）侧」的活动事实。
/// <para>
/// <b>为什么需要它：</b>改前所有静默归因只拿到 <c>logEnd</c>，于是
/// <c>logEnd - 请求时刻</c> 被当成「ECU 沉默时长」陈述。但日志在继续记录
/// 和测试仪还在发请求是两件不同的事：
/// </para>
/// <list type="bullet">
/// <item>ECU 沉默、测试仪继续发别的请求 → ECU 的问题。</item>
/// <item>测试仪自己停发（脚本崩了/被取消/操作员停机）→ 测试端的问题，日志末尾那段
/// 背景报文与 ECU 是否响应毫无关系。</item>
/// </list>
/// <para>
/// 生产日志 <c>150635_log.asc</c> 就是第二种：最后一条 tester 帧在 19.671s，
/// 日志却记到 28.569s，分析器据此报「ECU 未在 P2* 内响应 22.8s」——凭空造出一个故障。
/// </para>
/// </summary>
/// <param name="LastRequestTimestamp">最后一条测试仪请求的时间戳；无请求时为 null。</param>
/// <param name="LastRequestEndTimestamp">
/// 最后一条测试仪请求的<b>末帧</b>时间戳。多帧请求的首末帧可能差几十毫秒，
/// 用末帧才不会把传输耗时算成「ECU 的响应时间」。
/// </param>
public sealed record UdsTesterActivity(
    double? LastRequestTimestamp,
    double? LastRequestEndTimestamp)
{
    public static UdsTesterActivity Empty { get; } = new(null, null);

    /// <summary>
    /// 从已配对的事务里提取测试仪活动。
    /// <para>
    /// 只认请求 PDU：响应是 ECU 发的，不构成「测试仪还在工作」的证据。
    /// </para>
    /// </summary>
    public static UdsTesterActivity From(IReadOnlyList<UdsOfflineTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        if (transactions.Count == 0) return Empty;

        double? lastStart = null;
        double? lastEnd = null;
        foreach (var tx in transactions)
        {
            if (lastStart is null || tx.Request.FirstTimestamp > lastStart)
            {
                lastStart = tx.Request.FirstTimestamp;
                lastEnd = tx.Request.LastTimestamp;
            }
            else if (tx.Request.FirstTimestamp == lastStart
                     && tx.Request.LastTimestamp > (lastEnd ?? double.MinValue))
            {
                lastEnd = tx.Request.LastTimestamp;
            }
        }

        return new(lastStart, lastEnd);
    }
}
