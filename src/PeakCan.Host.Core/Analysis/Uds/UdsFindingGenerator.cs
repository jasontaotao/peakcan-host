namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>诊断阶段的完整输出：观测事实 + 结论 + 关系。</summary>
public sealed record UdsDiagnosisResult(
    IReadOnlyList<UdsObservation> Observations,
    IReadOnlyList<UdsFinding> Findings,
    IReadOnlyList<UdsFindingLink> Links);

/// <summary>
/// 把已解释的事务变成「有几个独立问题，各自优先排查哪一侧」。
/// <para>
/// <b>只做确定性规则，不做跨事务因果推断。</b>P0 阶段刻意不生成
/// <see cref="UdsFindingLink"/>：关联需要安全/会话状态机（P1），
/// 在没有状态机的情况下硬猜「A 导致 B」正是设计文档 §2.3 要避免的错误。
/// </para>
/// <para>
/// <b>文案纪律：</b><see cref="UdsFinding.Message"/> 只陈述日志事实。
/// 倾向性判断一律走 <see cref="UdsSuspect"/> 枚举，由 UI 渲染成「优先排查」。
/// 禁止出现「根因是」「一定是」。
/// </para>
/// </summary>
public sealed class UdsFindingGenerator
{
    private const byte NegativeResponseSid = 0x7F;

    private static readonly string[] EcuRejectionUnknown =
        { "ECU 拒绝该请求不证明拒绝不合理——可能是请求本身不满足条件" };

    private static readonly string[] FlashTruncationUnknown =
        { "刷写是否真的失败还是日志提前结束，仅凭本日志无法区分" };

    private static readonly string[] UnclosedFallbackUnknown =
        { "无法区分是日志截断、地址配置不符，还是记录中缺失了该响应对" };

    private static readonly string[] EcuSilenceUnknowns =
    {
        "日志在请求后仍在继续记录，因此「没收到响应」不是记录截断造成的",
        "但无法排除总线丢帧或地址配置错误（响应发到了别的 ID）",
    };

    private static readonly string[] TesterSilenceUnknowns =
    {
        "日志在上一活跃步骤后仍在继续记录，因此该步骤不是「没记到」",
        "但无法排除测试端主动中止（此时不继续发是正确行为）",
    };

    /// <summary>
    /// 测试仪在请求之后完全停止发送。
    /// <para>
    /// <b>这与「ECU 没响应」是两个完全不同的结论。</b>日志末尾的背景报文只证明
    /// 录制器还在跑，不证明 ECU 有义务回复——那时测试仪早已不在总线上。
    /// 生产日志 <c>150635_log.asc</c> 正是这个形态，却被报成 ECU 故障。
    /// </para>
    /// </summary>
    private static readonly string[] TesterStoppedUnknowns =
    {
        "日志在请求之后继续记录，但那是其它 ECU 的背景报文，与本请求无关",
        "测试仪未再发送任何报文，因此无法判断 ECU 是否最终会回复",
        "若测试端属主动停止（脚本结束、被取消、操作员中止），本条不构成异常",
    };

    /// <summary>测试仪没等响应就发了下一条——ECU 没机会回。</summary>
    private static readonly string[] TesterDidNotWaitUnknowns =
    {
        "测试仪未等待响应，因此无法判断 ECU 是否最终会回复",
        "若该请求本就允许不等待（如并行诊断流程），本条不构成异常",
    };

    /// <summary>
    /// 没有 ECU 声明 P2* 时用的保守阈值。取 <see cref="UdsTimer.DefaultP2StarTimeout"/>
    /// 的 5 秒：日志在请求后持续超过这个时间仍无响应，才认为「ECU 有机会回却没回」。
    /// </summary>
    private static readonly TimeSpan FallbackP2Star = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 没有 ECU 声明 P2 时用的值，只用于判断「测试仪有没有给 ECU 留响应时间」。
    /// <para>
    /// <b>方向必须是「小」而不是「大」：</b>判据是 <c>gap &lt; p2Seconds</c>，
    /// 阈值越大，越多「测试仪其实等够了」的间隔被判成「没等」，冤枉客户端。
    /// 取 ISO 14229-1 的典型默认 50 ms。
    /// </para>
    /// </summary>
    private const double FallbackP2Seconds = 0.05;

    /// <param name="transactions">已解释的事务。</param>
    /// <param name="logEnd">日志最后一个时间戳（秒）。</param>
    /// <param name="timing">ECU 在 <c>0x50</c> 里声明的时序；null 表示未知。</param>
    public UdsDiagnosisResult Generate(
        IReadOnlyList<UdsOfflineTransaction> transactions,
        double logEnd,
        UdsSessionTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        // <b>按时间排序，不依赖调用方的顺序。</b>`UdsTransactionBuilder.Build` 的输出是
        // 「已闭合（按闭合顺序）+ 仍开着（按发出顺序）」，并不是时间序；生产路径之所以
        // 有序，只是因为前面恰好跑了 `UdsSessionTimingTracker.Apply`（它内部排序）——
        // 一个「计时」组件顺手提供了「排序」语义。而 `NextTesterRequestAfter` 用索引
        // 向后扫描，一旦输入无序，就会把「测试仪还在发」误判成「测试仪停发」。
        var ordered = transactions.Count <= 1
            ? transactions
            : transactions.OrderBy(t => t.Request.FirstTimestamp).ToArray();

        var observations = new List<UdsObservation>();
        var findings = new List<UdsFinding>();
        var factsExtractor = new UdsFactsExtractor();

        // P2* 为 0 或负数说明 ECU 没有给出可用值（部分 ECU 用 0 表示「默认/不支持」）。
        // 直接拿它当窗口会让任何非零间隔都判成「ECU 有时间却没回」。
        var silenceWindow = timing is { P2StarMs: > 0 } t
            ? TimeSpan.FromMilliseconds(t.P2StarMs)
            : FallbackP2Star;

        // P2：请求到首个响应的时限。判「测试仪有没有等」用它，不用 P2*。
        var p2Seconds = timing is { P2Ms: > 0 } t2
            ? t2.P2Ms / 1000.0
            : FallbackP2Seconds;

        // 测试仪活动是归因的前提：没有它就无法区分「ECU 沉默」与「测试仪停发」。
        var tester = UdsTesterActivity.From(ordered);

        for (var i = 0; i < ordered.Count; i++)
        {
            var tx = ordered[i];
            var facts = factsExtractor.Extract(tx);

            if (tx.FinalKind == UdsFinalKind.Negative)
            {
                observations.Add(NegativeObservation(i, tx, facts));
                findings.Add(NegativeFinding(i, tx, facts, observations.Count - 1));
            }
            else if (tx.FinalKind == UdsFinalKind.None)
            {
                observations.Add(UnclosedObservation(i, tx, facts));
                findings.Add(UnclosedFinding(i, tx, observations.Count - 1,
                    ordered, i, logEnd, silenceWindow, p2Seconds, timing is not null, tester));
            }
            // Suppressed：ECU 按协议本不该回，不产生任何 finding。
        }

        // 刷写流程终止点：只在日志确实是刷写时才产出。
        var flash = new UdsFlashFlowAnalyzer().Analyze(ordered);
        if (flash.LooksLikeFlash && !flash.Completed && flash.BlockingStage is { } blocking)
        {
            observations.Add(FlashObservation(ordered, blocking, flash));
            findings.Add(FlashFinding(blocking, flash, observations.Count - 1,
                logEnd, silenceWindow, tester));
        }

        return new(observations, findings, Array.Empty<UdsFindingLink>());
    }

    private static UdsObservation NegativeObservation(
        int index, UdsOfflineTransaction tx, UdsOperationFacts facts)
    {
        var nrc = facts.Nrc ?? 0;
        var desc = UdsNrcSemantics.Describe(nrc);
        return new(
            Kind: "negative_response",
            TransactionIndex: index,
            FirstEvidenceIndex: tx.Request.FirstEvidenceIndex,
            LastEvidenceIndex: tx.Responses.Count > 0
                ? tx.Responses[^1].LastEvidenceIndex
                : tx.Request.LastEvidenceIndex,
            Timestamp: tx.Request.FirstTimestamp,
            Facts: facts,
            Detail: $"{facts.ServiceName} → {desc.Display}");
    }

    private static UdsFinding NegativeFinding(
        int index, UdsOfflineTransaction tx, UdsOperationFacts facts, int observationIndex)
    {
        var nrc = facts.Nrc ?? 0;
        var desc = UdsNrcSemantics.Describe(nrc);

        return new(
            Code: "NEGATIVE_RESPONSE",
            Severity: UdsFindingSeverity.Warn,
            Message: $"#{index + 1} {facts.ServiceName} {TargetOf(facts)} 被拒绝：{desc.Display}",
            ObservedAction: UdsObservedAction.EcuRejected,
            Suspect: desc.Suspect,
            ObservationIndexes: new[] { observationIndex },
            SuggestedNextCheck: NextCheck(nrc, facts),
            IsInference: false,
            Unknowns: EcuRejectionUnknown);
    }

    private static UdsObservation UnclosedObservation(
        int index, UdsOfflineTransaction tx, UdsOperationFacts facts)
        => new(
            Kind: "unclosed_request",
            TransactionIndex: index,
            FirstEvidenceIndex: tx.Request.FirstEvidenceIndex,
            LastEvidenceIndex: tx.Request.LastEvidenceIndex,
            Timestamp: tx.Request.FirstTimestamp,
            Facts: facts,
            Detail: $"{facts.ServiceName} 未收到通过校验的响应");

    /// <summary>
    /// 未闭合请求的归因。<b>按证据强度分派，不再只有一个「ECU 没响应」出口。</b>
    /// <para>
    /// 判据顺序（前面的优先级更高）：
    /// </para>
    /// <list type="number">
    /// <item><b>请求本身声明了抑制位</b> → 不该走到这里（builder 已归为 Suppressed）。</item>
    /// <item><b>收到过响应但校验不过</b> → 总线上有东西，问题在响应内容，不是「没响应」。</item>
    /// <item><b>响应重组不完整</b> → 传输层断了，与 ECU 内部状态无关。</item>
    /// <item><b>只收到 0x78</b> → ECU 明确在说「还在处理」，不能报「未响应」。</item>
    /// <item><b>测试仪随后发了别的请求</b> → 测试仪主动放弃，ECU 没机会回。</item>
    /// <item><b>测试仪之后完全停止</b> → 测试端停了，日志末尾的背景报文与此无关。</item>
    /// <item><b>测试仪一直在发、日志也继续</b> → 这才轮到「ECU 没回」。</item>
    /// </list>
    /// </summary>
    private static UdsFinding UnclosedFinding(
        int index,
        UdsOfflineTransaction tx,
        int observationIndex,
        IReadOnlyList<UdsOfflineTransaction> transactions,
        int txIndex,
        double logEnd,
        TimeSpan silenceWindow,
        double p2Seconds,
        bool timingKnown,
        UdsTesterActivity tester)
    {
        var requestEnd = tx.Request.LastTimestamp;
        var unknowns = tx.Unknowns.Count > 0 ? tx.Unknowns : UnclosedFallbackUnknown;

        // ── 1. 收到过响应但校验失败 ──────────────────────────────────────
        // 总线上确实有东西。说「ECU 没响应」会让用户去查一台回过话的 ECU。
        if (tx.ValidationFailedResponses > 0)
        {
            return new(
                Code: "UNCLOSED_REQUEST",
                Severity: UdsFindingSeverity.Warn,
                Message: $"#{index + 1} 请求未闭合：收到 {tx.ValidationFailedResponses} 个响应但未通过回显校验，" +
                          "无法确认它们回给的是本请求",
                ObservedAction: UdsObservedAction.RecordingInvalid,
                Suspect: UdsSuspect.Indeterminate,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: "对照原始证据确认这些响应的 DID / 子功能回显，" +
                                    "判断是并行诊断源还是本请求的回显不符",
                IsInference: true,
                Unknowns: unknowns);
        }

        // ── 2. 响应重组不完整 ────────────────────────────────────────────
        if (tx.IncompleteResponses > 0)
        {
            return new(
                Code: "UNCLOSED_REQUEST",
                Severity: UdsFindingSeverity.Warn,
                Message: $"#{index + 1} 请求未闭合：收到 {tx.IncompleteResponses} 个响应但 ISO-TP 重组不完整" +
                          "（多帧未拼完或序列中断）",
                ObservedAction: UdsObservedAction.RecordingIncomplete,
                Suspect: UdsSuspect.Indeterminate,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: "检查日志是否覆盖了完整的多帧响应，以及是否发生连续帧丢失",
                IsInference: true,
                Unknowns: unknowns);
        }

        // ── 3. 只收到 0x78 pending ───────────────────────────────────────
        // ECU 明确表示「还在处理」。每收到一次 0x78，P2* 重新计时。
        // <b>必须看最后一个 0x78 之后过了多久：</b>若日志在那之后又记了远超 P2* 的时间，
        // ECU 早就该给出最终响应了——此时再建议「延长记录时间」是错的，
        // 该说的是「ECU 超过了自己承诺的窗口」。
        if (tx.PendingCount > 0)
        {
            var lastPendingEnd = tx.Responses.Count > 0
                ? tx.Responses[^1].LastTimestamp
                : requestEnd;
            var sincePendingSeconds = Math.Max(logEnd - lastPendingEnd, 0);
            var pendingWindowExceeded = sincePendingSeconds > silenceWindow.TotalSeconds;

            return new(
                Code: "UNCLOSED_REQUEST",
                Severity: pendingWindowExceeded ? UdsFindingSeverity.Warn : UdsFindingSeverity.Info,
                Message: pendingWindowExceeded
                    ? $"#{index + 1} 请求未闭合：最后一次 0x78 pending 之后日志又记录了 " +
                      $"{sincePendingSeconds:F1}s（超过 P2* {silenceWindow.TotalSeconds:F1}s），" +
                      "ECU 未在承诺窗口内给出最终响应"
                    : $"#{index + 1} 请求未闭合：收到 {tx.PendingCount} 次 0x78 pending，" +
                      "日志结束前未见最终响应",
                // 动作值必须跟着判据走：只有真超窗才是「响应超时」，
                // 未超窗时 ECU 只是还在处理——说它超窗会与上面的「延长记录时间」建议矛盾。
                // 动作值必须跟着判据走：只有真超窗才是「响应超时」，
                // 未超窗时 ECU 只是还在处理——说它超窗会与上面的「延长记录时间」建议矛盾。
                ObservedAction: pendingWindowExceeded
                    ? UdsObservedAction.EcuRespondedLate
                    : UdsObservedAction.EcuStillProcessing,
                Suspect: UdsSuspect.Indeterminate,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: pendingWindowExceeded
                    ? "ECU 已超过自己声明的 P2*，检查其内部处理是否卡住；" +
                      "同时确认测试仪是否仍在等待"
                    : "延长记录时间；ECU 已表示在处理中，最终响应可能在日志之后到达",
                IsInference: true,
                Unknowns: unknowns);
        }

        // ── 4. 测试仪没等就发了下一条 → 主动放弃 ─────────────────────────
        // 判据是<b>测试仪给 ECU 留了多少时间</b>，不是「有没有下一条」。
        // 用 P2（收到请求到首个响应的时限），不是 P2*（那是 0x78 之后的续期）。
        // 测试仪发出下一条时若连 P2 都还没到，说明它根本没等——ECU 没有机会回。
        var nextTesterRequest = NextTesterRequestAfter(transactions, txIndex);
        if (nextTesterRequest is { } next)
        {
            var gapSeconds = next.Request.FirstTimestamp - requestEnd;
            if (gapSeconds < p2Seconds)
            {
                return new(
                    Code: "UNCLOSED_REQUEST",
                    Severity: UdsFindingSeverity.Info,
                    Message: $"#{index + 1} 请求未闭合：测试仪在 {gapSeconds * 1000:F0} ms 后就发出了下一条请求" +
                              $"（{next.Interpretation?.ServiceDisplay ?? "其它服务"}），" +
                              // P2 未知时 50ms 是自造阈值，不能以「ECU 声明的 P2」口吻陈述。
                              (timingKnown
                                  ? $"短于 ECU 声明的 P2（{p2Seconds * 1000:F0} ms），未给 ECU 留下响应时间"
                                  : $"短于保守等待阈值（{p2Seconds * 1000:F0} ms），未给 ECU 留下响应时间"),
                    ObservedAction: UdsObservedAction.ClientStopped,
                    Suspect: UdsSuspect.ClientSequencing,
                    ObservationIndexes: new[] { observationIndex },
                    SuggestedNextCheck: "确认测试端是否应该等待该请求的响应；" +
                                        "若属预期的异步流程，可忽略本条",
                    IsInference: true,
                    Unknowns: TesterDidNotWaitUnknowns);
            }
        }

        // ── 5. 日志在 P2* 之内就结束了 ──────────────────────────────────
        // 无论测试仪是否停止，观测窗口都不足以判定 ECU 会不会回。
        // 这条必须排在「测试仪停止」之前：否则 1 秒的日志也会被说成「测试仪停止」，
        // 而那更像是一次正常的短记录。
        var ecuWindowSeconds = logEnd - requestEnd;
        if (ecuWindowSeconds <= silenceWindow.TotalSeconds)
        {
            var why = timingKnown
                ? $"日志在请求后 {ecuWindowSeconds:F1}s 就结束了，未超过 ECU 声明的 P2*（{silenceWindow.TotalSeconds:F1}s）"
                : $"日志在请求后 {ecuWindowSeconds:F1}s 就结束了，未超过保守阈值 {silenceWindow.TotalSeconds:F0}s";

            return new(
                Code: "UNCLOSED_REQUEST",
                Severity: UdsFindingSeverity.Warn,
                Message: $"#{index + 1} 请求未闭合：{why}，无法判断 ECU 是否还会响应",
                ObservedAction: UdsObservedAction.RecordingIncomplete,
                Suspect: UdsSuspect.LogIntegrity,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: "延长记录时间重新抓一份，或确认日志是否在响应到达前被截断",
                IsInference: false,
                Unknowns: unknowns);
        }

        // ── 6. 测试仪之后完全停止 ───────────────────────────────────────
        // 这是用户生产日志的形态：tester 19.671s 停发，日志记到 28.569s。
        // 日志末尾的时长与 ECU 无关——那时测试仪早已不在总线上。
        //
        // 判据只看「这条请求之后测试仪还有没有发」：nextTesterRequest 为 null
        // 就说明没有。不能用「是不是全局最后一条请求」——一条未闭合的旧请求
        // 之后测试仪可能还发了别的，那种情况归到下面的 ECU 分支。
        var testerLastEnd = tester.LastRequestEndTimestamp ?? requestEnd;
        var testerIdleSeconds = Math.Max(logEnd - testerLastEnd, 0);

        if (nextTesterRequest is null)
        {
            return new(
                Code: "UNCLOSED_REQUEST",
                Severity: UdsFindingSeverity.Info,
                Message: $"#{index + 1} 请求未闭合：该请求（t={tx.Request.FirstTimestamp:F3}s）之后" +
                          $"测试仪未再发送任何报文（日志继续记录到 {logEnd:F3}s，共 {testerIdleSeconds:F1}s）",
                ObservedAction: UdsObservedAction.ClientStopped,
                Suspect: UdsSuspect.ClientSequencing,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: "确认测试端是否主动停止（脚本结束、被取消、或操作员中止）；" +
                                    "若测试端仍在运行，检查其日志与超时设置",
                IsInference: true,
                Unknowns: TesterStoppedUnknowns);
        }

        return new(
            Code: "UNCLOSED_REQUEST",
            Severity: UdsFindingSeverity.Warn,
            Message: $"#{index + 1} 请求未闭合：测试仪在请求后仍继续工作，" +
                      $"日志又记录了 {ecuWindowSeconds:F1}s，ECU 未在 P2*（{silenceWindow.TotalSeconds:F1}s）内响应",
            ObservedAction: UdsObservedAction.RecordingIncomplete,
            Suspect: UdsSuspect.EcuInternalState,
            ObservationIndexes: new[] { observationIndex },
            SuggestedNextCheck: "检查 ECU 是否处于可响应状态（会话、供电、诊断使能），" +
                                "以及该请求的地址是否确实被这台 ECU 服务",
            IsInference: true,
            Unknowns: EcuSilenceUnknowns);
    }

    /// <summary>
    /// 本请求之后的第一条测试仪请求。有它说明测试仪<b>没有</b>停，
    /// 只是没等这一条——两者的责任侧完全不同。
    /// </summary>
    private static UdsOfflineTransaction? NextTesterRequestAfter(
        IReadOnlyList<UdsOfflineTransaction> transactions, int txIndex)
    {
        for (var i = txIndex + 1; i < transactions.Count; i++)
        {
            if (transactions[i].Request.FirstTimestamp > transactions[txIndex].Request.LastTimestamp)
                return transactions[i];
        }
        return null;
    }


    private static UdsObservation FlashObservation(
        IReadOnlyList<UdsOfflineTransaction> transactions,
        UdsFlashStageKind blocking,
        UdsFlashFlow flash)
    {
        var stage = flash.Stages.First(s => s.Kind == blocking);
        var txIndex = stage.TransactionIndex;
        var tx = txIndex is { } idx && idx < transactions.Count ? transactions[idx] : null;

        // 证据范围要覆盖失败响应的帧，只取请求会漏掉「失败在哪一帧」。
        var lastEvidence = tx is { Responses.Count: > 0 }
            ? tx.Responses[^1].LastEvidenceIndex
            : tx?.Request.LastEvidenceIndex ?? 0;

        return new(
            Kind: "flash_incomplete",
            TransactionIndex: txIndex,
            FirstEvidenceIndex: tx?.Request.FirstEvidenceIndex ?? 0,
            LastEvidenceIndex: lastEvidence,
            Timestamp: stage.StartTimestamp ?? 0,
            Facts: null,
            Detail: $"刷写未完成，卡在 {blocking}");
    }

    /// <summary>
    /// 刷写流程终止点的归因。三种情况分开：
    /// <list type="number">
    /// <item>步骤出现过且被 ECU 拒绝 → ECU 拒绝（NRC 决定排查侧）。</item>
    /// <item>步骤没出现，但日志在其后仍继续记录 → <b>测试端该发没发</b>。</item>
    /// <item>步骤没出现，且日志到此结束 → 无法区分是没发还是没记到。</item>
    /// </list>
    /// <para>
    /// 第 2 种是最常见的真实情况：刷写脚本在某步崩了/被取消了，日志还在继续记。
    /// 把它说成「日志完整性」会让用户去查日志，而不是去查测试端。
    /// </para>
    /// </summary>
    private static UdsFinding FlashFinding(
        UdsFlashStageKind blocking,
        UdsFlashFlow flash,
        int observationIndex,
        double logEnd,
        TimeSpan silenceWindow,
        UdsTesterActivity tester)
    {
        var stage = flash.Stages.First(s => s.Kind == blocking);

        if (stage.Observed)
        {
            // 步骤出现了但没成功。要区分「ECU 回了负响应」与「压根没响应」：
            // 后者不是「ECU 拒绝」，把它说成拒绝会让用户去查一份根本没发生的拒绝。
            var rejected = stage.FailureDetail is { } d
                           && d.StartsWith("NRC 0x", StringComparison.Ordinal);

            var detail = stage.FailureDetail is { } text ? $"（{text}）" : "";
            return new(
                Code: "FLASH_INCOMPLETE",
                Severity: UdsFindingSeverity.Warn,
                Message: rejected
                    ? $"刷写未完成：卡在 {blocking}{detail}"
                    : $"刷写未完成：{blocking} 未收到最终响应{detail}",
                ObservedAction: rejected
                    ? UdsObservedAction.EcuRejected
                    : UdsObservedAction.RecordingIncomplete,
                Suspect: rejected
                    ? SuspectFromFailureDetail(stage.FailureDetail)
                    : UdsSuspect.Indeterminate,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: rejected
                    ? $"确认 {blocking} 之前的所有步骤是否完成"
                    : $"确认测试端是否在 {blocking} 之后停止发送，以及 ECU 当时是否处于可响应状态",
                IsInference: false,
                Unknowns: FlashTruncationUnknown);
        }

        // ── 步骤没出现 ───────────────────────────────────────────────────
        // 判据必须是「测试仪在流程终止后还有没有活动」，不是「日志还记了多久」。
        // 改前用各阶段的 StartTimestamp（首次出现）最大值——115 个传输块只算到第一块，
        // 于是「上一活跃步骤后又记录了 22.4s」比真实值大 2.5 倍（真实 8.9s）。
        var lastStageActivity = LastFlashActivity(flash);
        var testerLastEnd = tester.LastRequestEndTimestamp ?? lastStageActivity;

        // 流程终止之后测试仪是否还发过任何请求。
        var testerContinuedAfter = testerLastEnd > lastStageActivity + 1e-9;

        // 两个不同的量，别混用：
        //   afterStageSeconds = 流程终止 → 测试仪最后一条帧（「它还在工作时又过了多久」）
        //   idleSeconds       = 测试仪最后一条帧 → 日志结束（「它停发之后日志还记了多久」）
        // 改前把后者当成前者写进「仍继续发送了 X s」，句子与事实反向。
        var afterStageSeconds = Math.Max(testerLastEnd - lastStageActivity, 0);
        var idleSeconds = Math.Max(logEnd - testerLastEnd, 0);

        if (testerContinuedAfter)
        {
            return new(
                Code: "FLASH_INCOMPLETE",
                Severity: UdsFindingSeverity.Warn,
                Message: $"刷写未完成：卡在 {blocking}（该步骤未出现在日志中，" +
                          $"测试仪在流程终止后又继续发送了 {afterStageSeconds:F1}s）",
                // 测试仪还在工作，只是没发这一步——这是客户端流程问题，不是「停发」。
                // 也不能用 ClientOverlapped：那条枚举的含义是「两条请求同时在途」，
                // 而这里一条都没重叠，只是序列少了一步。
                ObservedAction: UdsObservedAction.ClientSkippedStep,
                Suspect: UdsSuspect.ClientSequencing,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: $"检查测试端为何没有发出 {blocking}（脚本中断、超时取消、前置步骤判定失败）",
                IsInference: true,
                Unknowns: TesterSilenceUnknowns);
        }

        // 测试仪也停了：无法区分「主动中止」与「崩溃」。
        if (idleSeconds > silenceWindow.TotalSeconds)
        {
            return new(
                Code: "FLASH_INCOMPLETE",
                Severity: UdsFindingSeverity.Warn,
                Message: $"刷写未完成：卡在 {blocking}（该步骤未出现在日志中，" +
                          $"测试仪在 t={testerLastEnd:F3}s 之后停止发送，日志继续记录到 {logEnd:F3}s）",
                ObservedAction: UdsObservedAction.ClientStopped,
                Suspect: UdsSuspect.ClientSequencing,
                ObservationIndexes: new[] { observationIndex },
                SuggestedNextCheck: "确认测试端是主动中止（脚本结束、被取消、操作员停机）还是异常退出",
                IsInference: true,
                Unknowns: TesterStoppedUnknowns);
        }

        return new(
            Code: "FLASH_INCOMPLETE",
            Severity: UdsFindingSeverity.Warn,
            Message: $"刷写未完成：卡在 {blocking}（该步骤未出现在日志中，" +
                      $"测试仪停发后日志只继续记录了 {idleSeconds:F1}s，未超过 P2*）",
            ObservedAction: UdsObservedAction.RecordingIncomplete,
            Suspect: UdsSuspect.LogIntegrity,
            ObservationIndexes: new[] { observationIndex },
            SuggestedNextCheck: "延长记录时间重新抓一份，确认日志是否覆盖完整刷写过程",
            IsInference: true,
            Unknowns: FlashTruncationUnknown);
    }

    /// <summary>
    /// 刷写流程里最后一次<b>测试仪活动</b>的时间。
    /// <para>
    /// 取各阶段 <see cref="UdsFlashStage.LastTimestamp"/> 的最大值——那是该阶段
    /// <b>最后一次</b>出现的时刻。不能用 <see cref="UdsFlashStage.StartTimestamp"/>：
    /// 115 个 TransferData 块只记到第一块（6.1s），真实最后一块在 19.6s。
    /// </para>
    /// <para>
    /// <b>也不能按 SID 扫全日志</b>：刷写卡住之后测试仪发的一条 `10 01`（回默认会话）
    /// 或无关的 `31` 例程会被算成「刷写还在进行」，于是「测试仪已停发」被误判成
    /// 「日志太短」。
    /// </para>
    /// </summary>
    private static double LastFlashActivity(UdsFlashFlow flash)
        => flash.Stages
            .Where(s => s.Observed)
            .Select(s => s.LastTimestamp ?? s.StartTimestamp ?? 0)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// 从 <c>NRC 0xNN Name</c> 形式的失败明细里取排查侧。
    /// 取不到时回落到「客户端前置顺序」——刷写步骤失败最常见的原因是前置步骤没做完。
    /// </summary>
    private static UdsSuspect SuspectFromFailureDetail(string? detail)
    {
        if (detail is null) return UdsSuspect.ClientSequencing;

        const string prefix = "NRC 0x";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal) || detail.Length < prefix.Length + 2)
            return UdsSuspect.ClientSequencing;

        return byte.TryParse(detail.AsSpan(prefix.Length, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var nrc)
            ? UdsNrcSemantics.Describe(nrc).Suspect
            : UdsSuspect.ClientSequencing;
    }

    /// <summary>
    /// 操作对象的可读描述。与 <see cref="UdsOfflineTransaction.TargetDisplay"/>
    /// 同一词表，避免同一件事在表格和结论里写法不一致。
    /// <para>
    /// <b><c>0x27</c> 不能漏：</b>安全访问失败是最常见的刷写断点之一，结论里只写
    /// <c>SecurityAccess</c> 等于没说是哪一级、哪个阶段——用户得回去翻原始帧。
    /// </para>
    /// </summary>
    private static string TargetOf(UdsOperationFacts f) => f switch
    {
        { DidId: { } did } => $"DID {did:X4}",
        { RoutineId: { } rid } => $"例程 {rid:X4}",
        { SessionType: { } s } => $"会话 {s:X2}",
        { BlockSequence: { } b } => $"block {b:X2}",
        // 与 UdsOfflineTransaction.SecurityAccessDisplay 同一措辞。
        { SecurityLevel: { } lvl } => $"安全等级 {lvl}",
        _ => f.ServiceName,
    };

    /// <summary>
    /// 下一步建议。优先用 NRC 表里的通用建议，再按操作对象补充更具体的一条。
    /// </summary>
    private static string? NextCheck(byte nrc, UdsOperationFacts facts)
    {
        var generic = UdsNrcSemantics.Describe(nrc).NextCheck;

        return (nrc, facts) switch
        {
            (0x31, { DidId: { } did }) => $"确认 DID 0x{did:X4} 存在且当前可写",
            (0x31, { RoutineId: { } rid }) => $"确认例程 0x{rid:X4} 存在且当前状态可执行",
            _ => generic,
        };
    }
}
