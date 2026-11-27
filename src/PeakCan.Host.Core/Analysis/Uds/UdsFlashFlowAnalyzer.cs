namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>刷写序列里的一个步骤。顺序即枚举顺序。</summary>
public enum UdsFlashStageKind
{
    ProgrammingSession,   // 0x10 03
    SecurityAccess,       // 0x27
    EraseOrPreCheck,      // 0x31
    RequestDownload,      // 0x34
    TransferData,         // 0x36
    RequestTransferExit,  // 0x37
    Verify,               // 0x31（校验类例程）
    EcuReset,             // 0x11
}

/// <summary>一个刷写步骤的观测结果。</summary>
/// <param name="Observed">日志里出现过该步骤的请求。</param>
/// <param name="Succeeded">出现且全部成功（重复步骤任一失败即为 false）。</param>
/// <param name="TransactionIndex">首次出现的事务序号；未出现为 null。</param>
/// <param name="StartTimestamp">首次出现的时间。</param>
/// <param name="FailureDetail">失败时的 NRC 说明。</param>
public sealed record UdsFlashStage(
    UdsFlashStageKind Kind,
    bool Observed,
    bool Succeeded,
    int? TransactionIndex,
    double? StartTimestamp,
    string? FailureDetail)
{
    /// <summary>
    /// 该阶段<b>最后一次</b>出现的时间。
    /// <para>
    /// <b>为什么必须有它：</b><see cref="StartTimestamp"/> 是首次出现。一次刷写有
    /// 115 个 TransferData 块时，用首次时间算「流程何时终止」会得到第一块（6.1s），
    /// 而真实最后一块在 19.6s——报给用户的空闲时长因此翻 2.5 倍。
    /// </para>
    /// </summary>
    public double? LastTimestamp { get; init; }
}

/// <summary>
/// 一次刷写尝试的流程画像。
/// <para>
/// <see cref="Completed"/> 判据刻意收紧：必须 <c>0x37</c> 成功 <b>且</b> 之后有
/// 成功的校验例程或 <c>0x11</c>。缺 <c>0x37</c> 一律 false——用户问「刷写到底
/// 成没成」，不能靠猜。
/// </para>
/// </summary>
public sealed record UdsFlashFlow(
    bool LooksLikeFlash,
    IReadOnlyList<UdsFlashStage> Stages,
    UdsFlashStageKind? LastSucceededStage,
    UdsFlashStageKind? BlockingStage,
    byte? LastTransferBlockSequence,
    int TransferBlockCount,
    bool Completed);

/// <summary>
/// 从已配对的事务里还原刷写流程。
/// <para>
/// <b>为什么不假设「A 失败导致 B 失败」：</b>设计文档 §2.3 的反例里
/// <c>0x27</c> 失败之后 <c>0x34</c> 仍然成功了。把后续失败都算到解锁失败头上
/// 会被日志本身证伪。本类只陈述每步各自的观测结果，不建立因果。
/// </para>
/// <para>
/// <b>为什么不要求完整序列：</b>OEM 私有服务的刷写序列各不相同。判据是
/// 「出现过任一刷写服务」，<see cref="LooksLikeFlash"/> 为 false 时 UI 直接不显示
/// 该区块，不报错。
/// </para>
/// </summary>
public sealed class UdsFlashFlowAnalyzer
{
    private const byte SessionControlSid = 0x10;
    private const byte EcuResetSid = 0x11;
    private const byte SecurityAccessSid = 0x27;
    private const byte RoutineControlSid = 0x31;
    private const byte RequestDownloadSid = 0x34;
    private const byte TransferDataSid = 0x36;
    private const byte RequestTransferExitSid = 0x37;

    private sealed class Acc
    {
        public bool Observed;
        public bool AllSucceeded = true;
        public int? FirstIndex;
        public double? FirstTs;

        /// <summary>该阶段<b>最后一次</b>出现的时间（不是首次）。</summary>
        public double? LastTs;

        public string? Failure;
    }

    public UdsFlashFlow Analyze(IReadOnlyList<UdsOfflineTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        var acc = new Dictionary<UdsFlashStageKind, Acc>();
        var blockSequences = new List<byte>();
        var anyFlashService = false;

        // 「这是不是刷写日志」看<b>整份日志</b>，不受「最后一次尝试从哪开始」影响。
        // 否则「第一次尝试完整传了数据、第二次尝试只发了个 0x31 就停」会被判成
        // 不是刷写——那显然不对。0x10 03 只说明进了编程会话，不算刷写证据。
        foreach (var tx in transactions)
        {
            var p = tx.Request.Payload;
            if (p.Length == 0) continue;
            if (p[0] is RequestDownloadSid or TransferDataSid or RequestTransferExitSid)
            {
                anyFlashService = true;
                break;
            }
        }

        // 每次「进入编程会话」视为一次新的刷写尝试。完成度只看最后一次——
        // 否则第一次走完后，第二次半途而废也会被报成「已完成」。
        // 日志从传输中途开始时没有 0x10 03，整体当作一次尝试。
        var lastAttemptStart = -1;
        for (var i = 0; i < transactions.Count; i++)
        {
            var p = transactions[i].Request.Payload;
            if (p.Length >= 2 && p[0] == SessionControlSid
                && (p[1] & UdsSubFunction.SubFunctionMask) == 0x03)
                lastAttemptStart = i;
        }
        var attemptFrom = lastAttemptStart >= 0 ? lastAttemptStart : 0;

        var requestTransferExitSucceeded = false;
        var postExitConfirmed = false;

        for (var i = attemptFrom; i < transactions.Count; i++)
        {
            var tx = transactions[i];
            var req = tx.Request.Payload;
            if (req.Length == 0) continue;

            var kind = Classify(req, transferExitSeen: requestTransferExitSucceeded);
            if (kind is null) continue;

            if (!acc.TryGetValue(kind.Value, out var a))
                acc[kind.Value] = a = new Acc();

            a.Observed = true;
            a.FirstIndex ??= i;
            a.FirstTs ??= tx.Request.FirstTimestamp;
            a.LastTs = tx.Request.LastTimestamp;

            // Suppressed 也算成功：ECU 被要求不回正响应，没有响应是预期行为。
            // 判成失败会让 `31 81 FF00`（擦除 + 抑制）这类合法步骤变成「ECU 拒绝了请求」。
            var ok = tx.FinalKind is UdsFinalKind.Positive or UdsFinalKind.Suppressed;

            // 失败记录分两种，不能一律「以最后一次为准」：
            //
            // ① <b>整步重试</b>（SecurityAccess / RequestTransferExit / Verify）：
            //    0x37 先回 NRC 0x24（顺序错），补发一次成功——流程是通的，
            //    此时「以最后一次为准」是对的。
            // ② <b>分块传输</b>（TransferData）：115 个块是 115 次独立请求，
            //    块 5 失败、块 6 成功不意味着块 5 补上了。按「最后一次为准」会把
            //    一次真实的丢块从报告里彻底抹掉。
            var isBlockTransfer = kind == UdsFlashStageKind.TransferData;
            if (isBlockTransfer)
            {
                // 只要有一次失败，整段传输就有问题——不被后续成功覆盖。
                if (!ok)
                {
                    a.AllSucceeded = false;
                    a.Failure ??= DescribeFailure(tx);
                }
            }
            else
            {
                a.AllSucceeded = ok;
                a.Failure = ok ? null : DescribeFailure(tx);
            }

            if (kind == UdsFlashStageKind.TransferData && ok && req.Length >= 2)
                blockSequences.Add(req[1]);

            if (kind == UdsFlashStageKind.RequestTransferExit && ok)
                requestTransferExitSucceeded = true;
            else if (requestTransferExitSucceeded
                     && ok
                     && kind is UdsFlashStageKind.Verify or UdsFlashStageKind.EcuReset)
                postExitConfirmed = true;
        }

        if (!anyFlashService)
            return new(false, Array.Empty<UdsFlashStage>(), null, null, null, 0, false);

        var stages = Enum.GetValues<UdsFlashStageKind>()
            .Select(k =>
            {
                var a = acc.GetValueOrDefault(k);
                return new UdsFlashStage(
                    k,
                    a?.Observed ?? false,
                    a is { Observed: true } && a.AllSucceeded,
                    a?.FirstIndex,
                    a?.FirstTs,
                    a?.Failure)
                {
                    LastTimestamp = a?.LastTs,
                };
            })
            .ToArray();

        var blocking = DetermineBlockingStage(stages, requestTransferExitSucceeded);

        // 按<b>时间</b>取最晚成功的阶段。改前按枚举顺序取 LastOrDefault，
        // 日志开头有一次成功的 0x11 复位时会把「最远到达阶段」报成 EcuReset。
        var lastSucceeded = stages
            .Where(s => s.Observed && s.Succeeded)
            .OrderBy(s => s.LastTimestamp ?? s.StartTimestamp ?? 0)
            .Select(s => (UdsFlashStageKind?)s.Kind)
            .LastOrDefault();

        return new(
            LooksLikeFlash: true,
            Stages: stages,
            LastSucceededStage: lastSucceeded,
            BlockingStage: blocking,
            LastTransferBlockSequence: blockSequences.Count > 0 ? blockSequences[^1] : null,
            TransferBlockCount: blockSequences.Count,
            Completed: requestTransferExitSucceeded && postExitConfirmed);
    }

    /// <summary>
    /// 找出流程<b>终止</b>在哪一步——按时间序，不是按枚举序。
    /// <para>
    /// <b>为什么不能取「最早失败的那步」：</b>设计文档 §2.3 的反例里
    /// <c>0x27</c> 失败之后 <c>0x34</c> 与 <c>0x36</c> 都成功了。取最早失败会
    /// 返回 SecurityAccess，于是 UI 显示「卡在 SecurityAccess（已传 2 块）」——
    /// 同一句话里自相矛盾，用户会以为后面的传输是幻觉。
    /// </para>
    /// <para>
    /// 判据：取<b>时间上最后一个失败步骤</b>；若之后还有成功的步骤，说明流程
    /// 已经越过它，则改看序列末端「该做没做」的步骤。
    /// </para>
    /// </summary>
    private static UdsFlashStageKind? DetermineBlockingStage(
        IReadOnlyList<UdsFlashStage> stages, bool transferExitSucceeded)
    {
        // 用<b>最后一次</b>出现时间排序。用首次时间会在「0x36 先出现、0x31 后出现」
        // 这类交错序列里判错终止点。
        var observed = stages
            .Where(s => s.Observed)
            .OrderBy(s => s.LastTimestamp ?? s.StartTimestamp ?? 0)
            .ToArray();
        if (observed.Length == 0) return null;

        var lastFailed = observed.LastOrDefault(s => !s.Succeeded);
        var lastObserved = observed[^1];

        // 最后观测到的步骤本身失败了 → 它就是终止点。
        if (lastFailed is not null && lastFailed.Kind == lastObserved.Kind)
            return lastFailed.Kind;

        // 末端「该做没做」：传完所有块却没发 0x37。
        if (lastObserved.Kind == UdsFlashStageKind.TransferData && !transferExitSucceeded)
            return UdsFlashStageKind.RequestTransferExit;

        // 0x37 成功了但没有校验/复位 —— 流程停在「本该校验」。
        if (transferExitSucceeded && lastObserved.Kind == UdsFlashStageKind.RequestTransferExit)
            return UdsFlashStageKind.Verify;

        // 有更早的失败、末端是成功步骤时：若那个失败步骤<b>没有被后续成功越过</b>
        // （即它属于必经序列、而序列并没有真的走完），它才是真正的断点。
        // 例：0x27 失败 → 直接发 0x34（跳过了擦除）→ 0x34 成功。
        // 此时末端补缺口会说「缺 TransferData」，但真正的问题是安全访问没过。
        if (lastFailed is not null)
        {
            var nextAfterFailed = NextExpectedStage(lastFailed.Kind);
            var failureWasSkippedOver = nextAfterFailed is null
                || !observed.Any(s => s.Kind == nextAfterFailed && s.Succeeded);
            if (failureWasSkippedOver) return lastFailed.Kind;
        }

        // 停在序列中段的任何一步：按「正常刷写序列里该步的下一步」补出缺口。
        // <b>改前这里直接 return null</b>，于是「进入编程会话后停住」「解锁后停住」
        // 「RequestDownload 后停住」都不产出任何刷写结论——UI 只能显示
        // 「卡在未知步骤」，用户拿不到任何线索。
        var next = NextExpectedStage(lastObserved.Kind);
        if (next is not null) return next;

        // 末端之前有过失败，但之后还成功了 → 那个失败已被越过，不算终止点。
        return null;
    }

    /// <summary>失败明细：有 NRC 写 NRC，没有就是「未收到最终响应」。</summary>
    private static string DescribeFailure(UdsOfflineTransaction tx)
        => tx.FinalNrc is { } nrc
            ? $"NRC 0x{nrc:X2} {UdsNrcSemantics.Describe(nrc).Name ?? "(未知)"}"
            : "未闭合";

    /// <summary>
    /// 正常刷写序列里某一步的下一步。用于「流程停在中段」时补出缺口。
    /// <para>
    /// 刻意不覆盖 <see cref="UdsFlashStageKind.EcuReset"/>：复位是流程结束后的动作，
    /// 没有它不算缺口。
    /// </para>
    /// </summary>
    private static UdsFlashStageKind? NextExpectedStage(UdsFlashStageKind current) => current switch
    {
        UdsFlashStageKind.ProgrammingSession => UdsFlashStageKind.SecurityAccess,
        UdsFlashStageKind.SecurityAccess => UdsFlashStageKind.EraseOrPreCheck,
        UdsFlashStageKind.EraseOrPreCheck => UdsFlashStageKind.RequestDownload,
        UdsFlashStageKind.RequestDownload => UdsFlashStageKind.TransferData,
        UdsFlashStageKind.Verify => UdsFlashStageKind.EcuReset,
        _ => null,
    };

    /// <summary>
    /// 给一条请求归类到刷写步骤。
    /// <para>
    /// <b>为什么 <c>0x31</c> 按位置而不是按例程 ID 区分「擦除」与「校验」：</b>
    /// 例程 ID 是 OEM 私有的，<c>0xFF00</c> 在多数实现里是 eraseMemory 而不是校验。
    /// 用 ID 段去猜会把擦除误标成校验，进而让 <see cref="UdsFlashFlow.Completed"/>
    /// 变成假阳性。位置判据（<c>0x37</c> 之后的 <c>0x31</c> 才是校验）不依赖 OEM
    /// 约定，且与本类的「不猜」原则一致。
    /// </para>
    /// </summary>
    private static UdsFlashStageKind? Classify(byte[] req, bool transferExitSeen)
    {
        switch (req[0])
        {
            case SessionControlSid when req.Length >= 2:
                return req[1] == 0x03 ? UdsFlashStageKind.ProgrammingSession : null;

            case EcuResetSid when req.Length >= 2:
                return UdsFlashStageKind.EcuReset;

            case SecurityAccessSid when req.Length >= 2:
                // seed 与 key 两个阶段都归到 SecurityAccess 阶段：
                // seed 被拒同样让整步无法完成，忽略它会让「0x27 01 就失败」的日志
                // 显示成「没有安全访问步骤」。
                return UdsFlashStageKind.SecurityAccess;

            case RoutineControlSid when req.Length >= 4:
                return transferExitSeen
                    ? UdsFlashStageKind.Verify
                    : UdsFlashStageKind.EraseOrPreCheck;

            case RequestDownloadSid:
                return UdsFlashStageKind.RequestDownload;

            case TransferDataSid:
                return UdsFlashStageKind.TransferData;

            case RequestTransferExitSid:
                return UdsFlashStageKind.RequestTransferExit;

            default:
                return null;
        }
    }
}
