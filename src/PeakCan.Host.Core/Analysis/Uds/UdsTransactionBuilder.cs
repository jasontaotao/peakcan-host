namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Third stage of the offline pipeline: pair request PDUs with their responses.
/// Pairing is intentionally wall-clock-free — a log holds the whole exchange, so
/// a response arriving "too late" for a live stack is still a valid pairing here
/// (Task 5 records the delay as timing, not as a discard reason).
/// </summary>
public sealed class UdsTransactionBuilder
{
    private const byte NegativeResponseSid = 0x7F;
    private const byte PendingNrc = 0x78;
    private const byte PositiveResponseOffset = 0x40;

    private sealed class OpenTx
    {
        public required UdsOfflinePdu Request { get; init; }
        public byte Sid => Request.Payload[0];
        public List<UdsOfflinePdu> Responses { get; } = new();
        public int PendingCount;
        public byte? Nrc;
        public UdsFinalKind Final = UdsFinalKind.None;
        public bool Ambiguous;
        public bool Duplicate;

        public UdsOfflineTransaction ToTransaction() => new(
            Request, Responses, Final, PendingCount, Nrc,
            IsOrphan: false, IsAmbiguous: Ambiguous,
            HasDuplicateFinalResponse: Duplicate,
            Unknowns: Array.Empty<string>());
    }

    public UdsTransactionBuildResult Build(IReadOnlyList<UdsOfflinePdu> pdus)
    {
        ArgumentNullException.ThrowIfNull(pdus);

        var open = new List<OpenTx>();
        var closed = new List<OpenTx>();
        var orphans = new List<UdsOfflinePdu>();
        var failed = new List<UdsOfflinePdu>();
        var duplicates = new List<UdsOfflinePdu>();

        foreach (var pdu in pdus)
        {
            if (pdu.Payload.Length == 0) continue; // 空 PDU 不会由重组器产出，防御性跳过

            if (pdu.Role == UdsDirectionRole.Request)
            {
                // 同一 SID 的第二个请求开着 = 配对本身不可确定，标记歧义但不丢。
                var sameSid = open.FirstOrDefault(x => x.Sid == pdu.Payload[0]);
                if (sameSid is not null) sameSid.Ambiguous = true;
                open.Add(new OpenTx { Request = pdu });
                continue;
            }

            var resSid = pdu.Payload[0];

            if (resSid == NegativeResponseSid)
            {
                if (pdu.Payload.Length < 3)
                {
                    failed.Add(pdu);
                    continue;
                }
                var reqSid = pdu.Payload[1];
                var nrc = pdu.Payload[2];
                var tx = open.FirstOrDefault(x => x.Sid == reqSid);
                if (tx is null)
                {
                    orphans.Add(pdu);
                    continue;
                }
                tx.Responses.Add(pdu);
                if (nrc == PendingNrc)
                {
                    tx.PendingCount++;
                }
                else
                {
                    tx.Nrc = nrc;
                    tx.Final = UdsFinalKind.Negative;
                    open.Remove(tx);
                    closed.Add(tx);
                }
                continue;
            }

            var expectedReqSid = (byte)(resSid - PositiveResponseOffset);
            var target = open.FirstOrDefault(x => x.Sid == expectedReqSid);
            if (target is null)
            {
                // 已闭合事务再收到最终响应：不能静默变成“孤立响应”。
                var closedTarget = closed.LastOrDefault(x => x.Sid == expectedReqSid && !x.Duplicate);
                if (closedTarget is not null)
                {
                    closedTarget.Responses.Add(pdu);
                    closedTarget.Duplicate = true;
                    closedTarget.Ambiguous = true;
                    duplicates.Add(pdu);
                }
                else
                {
                    orphans.Add(pdu);
                }
                continue;
            }

            // 日志在传输中途结束：payload 被截断，不能当作“完整正响应”闭合事务。
            if (!pdu.IsComplete)
            {
                failed.Add(pdu);
                continue;
            }

            if (!UdsResponseValidator.TryValidate(target.Request, pdu, out _))
            {
                failed.Add(pdu);
                continue;
            }

            target.Responses.Add(pdu);
            target.Final = UdsFinalKind.Positive;
            open.Remove(target);
            closed.Add(target);
        }

        var transactions = closed.Select(x => x.ToTransaction()).ToList();
        transactions.AddRange(open.Select(x => x.ToTransaction()));
        return new(transactions, orphans, failed, duplicates);
    }
}
