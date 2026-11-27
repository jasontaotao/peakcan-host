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

        /// <summary>
        /// 收到过针对本请求的响应、但没通过回显校验的次数。
        /// 与「完全没收到响应」是两种不同的不可知，用户必须能区分。
        /// </summary>
        public int ValidationFailedResponses;

        /// <summary>
        /// 收到过针对本请求的响应、但 ISO-TP 重组不完整（多帧没拼完）的次数。
        /// 与「没收到响应」和「校验失败」都不同：总线上确实有东西，
        /// 只是日志在传输中途结束或序列断了。
        /// </summary>
        public int IncompleteResponses;

        /// <summary>
        /// 请求声明了 suppressPosRspMsgIndicationBit：正响应不会到来，
        /// 但<b>负响应仍会</b>。正响应不能闭合它（否则它会吃掉后续同 SID 请求的响应）。
        /// </summary>
        public bool ExpectsNoPositiveResponse;

        public UdsOfflineTransaction ToTransaction() => new(
            Request, Responses, Final, PendingCount, Nrc,
            IsOrphan: false, IsAmbiguous: Ambiguous,
            HasDuplicateFinalResponse: Duplicate,
            Unknowns: BuildUnknowns())
        {
            // 归因需要计数本身，不能只靠 Unknowns 文案——文案改动会让归因静默失效。
            ValidationFailedResponses = ValidationFailedResponses,
            IncompleteResponses = IncompleteResponses,
        };

        /// <summary>
        /// 未闭合时列出「为什么没闭合」。已闭合（正/负响应）没有不可知项。
        /// <para>
        /// <b>绝不能写「ECU 未发送」</b>（spec §16 验收第 5 条）：日志只证明
        /// 「这份记录里没有可用的响应」，不能反推总线上发生过什么。
        /// </para>
        /// </summary>
        private IReadOnlyList<string> BuildUnknowns()
        {
            if (Final != UdsFinalKind.None) return Array.Empty<string>();

            var list = new List<string>(3);

            if (ValidationFailedResponses > 0)
            {
                list.Add($"收到 {ValidationFailedResponses} 个响应但未通过回显校验，" +
                         "无法确认它们是回给本请求的");
            }
            else if (IncompleteResponses > 0)
            {
                list.Add($"收到 {IncompleteResponses} 个响应但 ISO-TP 重组不完整" +
                         "（多帧未拼完或序列中断）");
            }
            else if (Responses.Count > 0)
            {
                list.Add("仅收到 0x78 pending 响应，日志结束前未见最终响应");
            }
            else
            {
                list.Add("日志结束前未观测到任何属于本请求的响应");
            }

            list.Add("无法区分是日志截断、地址配置不符，还是记录中缺失了该响应对");

            if (Ambiguous)
                list.Add("同 SID 请求重叠，本事务的响应归属仅供参考");

            return list;
        }
    }

    public UdsTransactionBuildResult Build(IReadOnlyList<UdsOfflinePdu> pdus)
    {
        ArgumentNullException.ThrowIfNull(pdus);

        var open = new List<OpenTx>();
        var closed = new List<OpenTx>();
        var orphans = new List<UdsOfflinePdu>();
        var failed = new List<UdsOfflinePdu>();
        var duplicates = new List<UdsOfflinePdu>();
        var failures = new List<UdsResponseValidationFailure>();

        foreach (var pdu in pdus)
        {
            if (pdu.Payload.Length == 0) continue; // 空 PDU 不会由重组器产出，防御性跳过

            if (pdu.Role == UdsDirectionRole.Request)
            {
                // 声明了「不要回正响应」的请求（如 3E 80）：ECU 不回正响应，
                // 但<b>仍可能回负响应</b>——ISO 14229-1 里 suppressPosRspMsgIndicationBit
                // 只抑制正响应。
                //
                // 所以它必须留在等待队列里（否则 `7F 31 22` 会变成孤立响应，NRC 丢失，
                // 刷写还会把它判成成功）；只是标记「不接受正响应闭合」，让正响应
                // 落不到它头上——否则它会吃掉下一条同 SID 请求的响应。
                var sameSidOpen = open.FirstOrDefault(x => x.Sid == pdu.Payload[0]);
                if (sameSidOpen is not null) sameSidOpen.Ambiguous = true;
                open.Add(new OpenTx
                {
                    Request = pdu,
                    ExpectsNoPositiveResponse = UdsSubFunction.SuppressesPositiveResponse(pdu.Payload),
                });
                continue;
            }

            var resSid = pdu.Payload[0];

            if (resSid == NegativeResponseSid)
            {
                // 负响应长度必须恰为 3（SID 0x7F + 请求 SID + NRC）；带尾巴的
                // 周期帧不算最终 NRC（spec §8.5）。
                if (pdu.Payload.Length != 3)
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

            // 抑制了正响应的请求不接受正响应闭合：正响应属于后来的同 SID 请求。
            // 若把它配给抑制请求，真正的请求会「未闭合」——这正是生产日志里
            // `3E 80` 吃掉 `3E 00` 响应的那个错配。
            var target = open.FirstOrDefault(x => x.Sid == expectedReqSid && !x.ExpectsNoPositiveResponse);
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
            // 但要记在事务上——「响应被截断」和「压根没响应」要能分开说。
            if (!pdu.IsComplete)
            {
                failed.Add(pdu);
                target.IncompleteResponses++;
                continue;
            }

            if (!UdsResponseValidator.TryValidate(target.Request, pdu, out var failedCheck))
            {
                failed.Add(pdu);
                target.ValidationFailedResponses++;
                failures.Add(new UdsResponseValidationFailure(
                    failedCheck ?? "Unknown",
                    pdu.FirstEvidenceIndex,
                    target.Request.FirstEvidenceIndex));
                continue;
            }

            target.Responses.Add(pdu);
            target.Final = UdsFinalKind.Positive;
            open.Remove(target);
            closed.Add(target);
        }

        // 日志结束时仍开着的抑制请求：ECU 没有回负响应，说明请求被接受了。
        // 标成 Suppressed（而不是 None）——None 意味着「该有没有」，会触发
        // 「ECU 未响应」的归因，而这里没有响应正是协议要求的。
        foreach (var tx in open)
        {
            if (tx.ExpectsNoPositiveResponse) tx.Final = UdsFinalKind.Suppressed;
        }

        var transactions = closed.Select(x => x.ToTransaction()).ToList();
        transactions.AddRange(open.Select(x => x.ToTransaction()));
        return new(transactions, orphans, failed, duplicates)
        {
            ValidationFailureDetails = failures,
        };
    }
}
