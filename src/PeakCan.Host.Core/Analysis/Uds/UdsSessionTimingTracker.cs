namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Fourth stage of the offline pipeline: derive per-transaction durations and
/// the session's P2 / P2* values. Timing is informational — the first version
/// never drops or reclassifies a transaction because it was "too slow"; the
/// report marks the delay and leaves the judgement to the reader.
/// </summary>
public sealed class UdsSessionTimingTracker
{
    private const byte SessionControlSid = 0x10;
    private const byte SessionControlPositiveSid = 0x50;
    private const string EcuSource = "ECU:0x50";
    private const string UnknownSource = "Unknown";

    public UdsSessionTiming? Timing { get; private set; }

    public IReadOnlyList<UdsOfflineTransaction> Apply(IReadOnlyList<UdsOfflineTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var result = new List<UdsOfflineTransaction>(transactions.Count);
        foreach (var tx in transactions.OrderBy(t => t.Request.FirstTimestamp))
        {
            // 会话控制事务本身也应显示它自己声明的 P2/P2* 来源，
            // 所以先读时序再投影时间字段。
            if (TryReadSessionTiming(tx, out var timing)) Timing = timing;

            var first = tx.Responses.Count == 0 ? null : tx.Responses[0];
            var firstMs = first is null
                ? (double?)null
                : (first.FirstTimestamp - tx.Request.LastTimestamp) * 1000.0;
            var totalMs = tx.FinalKind == UdsFinalKind.None || tx.Responses.Count == 0
                ? (double?)null
                : (tx.Responses[^1].LastTimestamp - tx.Request.FirstTimestamp) * 1000.0;

            result.Add(tx with
            {
                FirstResponseMs = firstMs,
                TotalMs = totalMs,
                TimingSource = Timing?.Source ?? UnknownSource,
            });
        }
        return result;
    }

    private static bool TryReadSessionTiming(UdsOfflineTransaction tx, out UdsSessionTiming timing)
    {
        timing = default!;
        if (tx.Request.Payload.Length == 0 || tx.Request.Payload[0] != SessionControlSid) return false;
        if (tx.FinalKind != UdsFinalKind.Positive || tx.Responses.Count == 0) return false;

        var res = tx.Responses[^1].Payload;
        if (res.Length < 6 || res[0] != SessionControlPositiveSid || res[1] != tx.Request.Payload[1])
            return false;

        var p2 = (res[2] << 8) | res[3];
        var p2Star = ((res[4] << 8) | res[5]) * 10; // P2* 单位为 10 ms
        timing = new UdsSessionTiming(p2, p2Star, EcuSource);
        return true;
    }
}
