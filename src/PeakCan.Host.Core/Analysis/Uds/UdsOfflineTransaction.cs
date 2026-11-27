namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>How a request's exchange ended. <see cref="None"/> = still open at log end.</summary>
public enum UdsFinalKind
{
    Positive,
    Negative,
    None,

    /// <summary>
    /// 请求声明了 <c>suppressPosRspMsgIndicationBit</c>（如 <c>3E 80</c>），
    /// ECU 按协议<b>不应</b>回正响应——没有响应是预期行为，不是故障。
    /// <para>
    /// <b>必须与 <see cref="None"/> 分开：</b>两者都没有响应，但
    /// <see cref="None"/> 是「该有没有」，<see cref="Suppressed"/> 是「本来就不该有」。
    /// 混在一起会让每一次刷写 keepalive 都产生一条假的「ECU 不响应」告警。
    /// </para>
    /// </summary>
    Suppressed,
}

/// <summary>
/// One request paired with the responses seen for it. <see cref="IsAmbiguous"/>
/// is set when two same-SID requests were open at once or a duplicate final
/// response arrived — the pairing is then a best guess, and the report says so
/// instead of presenting it as fact.
/// </summary>
public sealed record UdsOfflineTransaction(
    UdsOfflinePdu Request,
    IReadOnlyList<UdsOfflinePdu> Responses,
    UdsFinalKind FinalKind,
    int PendingCount,
    byte? FinalNrc,
    bool IsOrphan,
    bool IsAmbiguous,
    bool HasDuplicateFinalResponse,
    IReadOnlyList<string> Unknowns)
{
    public int EvidenceIndex => Request.FirstEvidenceIndex;
    public double FirstTimestamp => Request.FirstTimestamp;

    /// <summary>
    /// 收到过、但没通过回显校验的响应个数。
    /// <para>
    /// <b>归因要用它，不能靠解析 <see cref="Unknowns"/> 的文案：</b>文案是给人看的，
    /// 改一个字就会让归因静默失效。改前 <see cref="UdsFindingGenerator"/> 只看到
    /// <c>FinalKind.None</c>，就一律判「ECU 没响应」——即使总线上明明有东西。
    /// </para>
    /// </summary>
    public int ValidationFailedResponses { get; init; }

    /// <summary>收到过、但 ISO-TP 重组不完整的响应个数（与「没响应」是两回事）。</summary>
    public int IncompleteResponses { get; init; }

    /// <summary>请求结束到首个响应的毫秒数；没有响应时为 null。</summary>
    public double? FirstResponseMs { get; init; }

    /// <summary>请求开始到最终响应的毫秒数；事务未闭合时为 null（不能拿“最后一个响应”冒充总耗时）。</summary>
    public double? TotalMs { get; init; }

    /// <summary>P2/P2* 的来源事务（例如 <c>ECU:0x50</c>）；无会话控制响应时为 <c>Unknown</c>。</summary>
    public string TimingSource { get; init; } = "Unknown";

    /// <summary>服务解释结果；由 <see cref="UdsOfflineAnalyzer"/> 在报告组装阶段填充。</summary>
    public UdsInterpretation? Interpretation { get; init; }

    /// <summary>
    /// 首个响应耗时超过 ECU 声明的 P2（只做标记，不剔除事务）。没有会话控制响应时恒为 false。
    /// </summary>
    public bool ExceedsP2 { get; init; }

    /// <summary>
    /// 这个事务在操作什么——<c>DID F190</c> / <c>例程 FF00</c> / <c>会话 03</c> /
    /// <c>block 03</c>。从请求 payload 直接解析，**负响应同样有值**：
    /// 「写 DID 失败」必须能显示是哪个 DID，否则用户不知道该查什么。
    /// </summary>
    public string TargetDisplay
    {
        get
        {
            var req = Request.Payload;
            if (req.Length == 0) return "—";

            return req[0] switch
            {
                // 掩掉抑制位：10 83 的会话是 03，不是 83。
                0x10 when req.Length >= 2 => $"会话 {(byte)(req[1] & UdsSubFunction.SubFunctionMask):X2}",
                0x22 or 0x2E when req.Length >= 3 => $"DID {(ushort)((req[1] << 8) | req[2]):X4}",
                // 等级是**推导值**（ISO 约定 (sub+1)/2），子功能字节才是**事实**。
                // 只显示等级时，用户看着日志里的 27 09 / 27 0A 无法与界面上的「5 级」对上，
                // 也就无法确认分析器算的是不是他要的那个等级。两个都给。
                0x27 when req.Length >= 2 => SecurityAccessDisplay(req[1]),
                0x31 when req.Length >= 4 => $"例程 {(ushort)((req[2] << 8) | req[3]):X4}",
                0x36 when req.Length >= 2 => $"block {req[1]:X2}",
                _ => "—",
            };
        }
    }

    /// <summary>
    /// <c>0x27</c> 的显示：安全等级 + 原始子功能字节。
    /// <para>
    /// <b>为什么两个都要给：</b>等级是按 ISO 14229-1 的约定 <c>(sub + 1) / 2</c> 推出来的，
    /// 不是日志里的事实。只显示「安全等级 5」时，用户对着日志里的 <c>27 09</c> /
    /// <c>27 0A</c> 对不上——既无法确认分析器算的是不是他要的等级，也无法把这一行
    /// 和原始帧关联起来。子功能字节是事实，等级是推导，展示时不能只留推导。
    /// </para>
    /// <para>
    /// 显示的是<b>掩掉抑制位后</b>的子功能：<c>27 89</c> 的有效子功能是 <c>0x09</c>，
    /// 抑制位不影响等级也不影响 seed/key 阶段。
    /// </para>
    /// </summary>
    private static string SecurityAccessDisplay(byte rawSub)
    {
        var sub = (byte)(rawSub & UdsSubFunction.SubFunctionMask);
        var level = (byte)((sub + 1) / 2);
        // 奇数请求 seed，偶数发送 key（ISO 14229-1 §10.2）。
        var step = (sub & 1) == 1 ? "请求种子" : "发送密钥";
        return $"安全等级 {level}（子功能 0x{sub:X2} {step}）";
    }

    /// <summary>
    /// 最终 NRC 的标准名；未知值或非负响应为 null。
    /// 只显示 hex 时用户不知道 <c>0x22</c> 与 <c>0x33</c> 是完全不同的两回事。
    /// </summary>
    public string? NrcName => FinalNrc is { } nrc ? UdsNrcSemantics.Describe(nrc).Name : null;

    /// <summary>
    /// 这个事务的响应<b>实际</b>校验到了什么程度（spec §8.5 最后一条）。
    /// <para>
    /// <b>必须看结局，不能只看请求 SID。</b>负响应在
    /// <see cref="UdsTransactionBuilder"/> 里只校验「长度恰为 3」，
    /// 从不走 <see cref="UdsResponseValidator.TryValidate"/>；未闭合事务更是
    /// 根本没有最终响应。若只按请求 SID 查表，<c>0x2E → 7F 2E 33</c>（写 DID
    /// 被拒，最常见的失败形态）会显示「已校验回显」——恰好是这次改动要消灭的
    /// 「冒充已校验」，而且发生在用户最需要看失败原因的事务上。
    /// </para>
    /// </summary>
    public UdsEchoCheckLevel EchoCheck => FinalKind switch
    {
        // 正响应才真的过了回显校验；查表得到的就是实际强度。
        UdsFinalKind.Positive when Request.Payload.Length > 0
            => UdsResponseValidator.EchoCheckLevelFor(Request.Payload[0]),
        // 负响应只验了 3 字节长度，任何回显字段都没比对。
        // 未闭合：没有最终响应，谈不上校验。
        _ => UdsEchoCheckLevel.NotChecked,
    };

    /// <summary>
    /// 校验强度的中文陈述。<b>唯一来源</b>——界面、导出、AI 提示词都调它，
    /// 三处不得各自拼字面量（此前已经漂移出「已校验回显」/「已校验响应回显」两种说法）。
    /// </summary>
    public string EchoCheckDisplay => FinalKind == UdsFinalKind.Suppressed
        ? "无需校验（响应已抑制）"
        : EchoCheck switch
        {
            UdsEchoCheckLevel.FullEcho => "已校验回显",
            UdsEchoCheckLevel.LengthOnly => "仅长度校验（内容未比对）",
            _ => "未校验（无最终正响应）",
        };

    /// <summary>有没有不可判定项（XAML 的触发器不能直接判断集合计数）。</summary>
    public bool HasUnknowns => Unknowns.Count > 0;

    /// <summary>
    /// 「结果」列的中文文本。直接绑枚举会在中文界面里显示 <c>Suppressed</c>
    /// 这类英文标识符——用户看不懂，而且与其它中文列风格割裂。
    /// </summary>
    public string FinalKindDisplay => FinalKind switch
    {
        UdsFinalKind.Positive => "正响应",
        UdsFinalKind.Negative => "负响应",
        UdsFinalKind.Suppressed => "响应抑制",
        _ => "未闭合",
    };

    /// <summary>
    /// 事务表「标记」列的紧凑文本：把 P2 超限 / 归属歧义 / 重复最终响应
    /// 合成一列短码。<b>为什么要合并：</b>三个独立布尔列各占 40–80 px，
    /// 加上其它列会超过默认窗口宽度，把「服务」列压到不可读。
    /// 短码 + ToolTip 说明既省宽度又不丢信息。
    /// </summary>
    public string FlagsDisplay
    {
        get
        {
            var flags = new List<string>(3);
            if (ExceedsP2) flags.Add("P2超");
            if (IsAmbiguous) flags.Add("歧义");
            if (HasDuplicateFinalResponse) flags.Add("重复");
            return flags.Count == 0 ? "—" : string.Join("/", flags);
        }
    }

    /// <summary>「标记」列的完整说明（ToolTip 用）。</summary>
    public string FlagsTooltip
    {
        get
        {
            var lines = new List<string>(3);
            if (ExceedsP2) lines.Add("首个响应晚于 ECU 声明的 P2");
            if (IsAmbiguous) lines.Add("同 SID 请求重叠，配对仅供参考");
            if (HasDuplicateFinalResponse) lines.Add("收到重复最终响应，不判确定成功");
            return lines.Count == 0 ? "无标记" : string.Join("\n", lines);
        }
    }
}

/// <summary>
/// 一条回显校验失败的明细。
/// <para>
/// 改前 <see cref="UdsResponseValidator.TryValidate"/> 用 <c>out _</c> 丢弃了
/// <c>FailedCheck</c>，报告只剩「响应校验失败 N」这个总数——用户知道有几帧没过，
/// 但不知道失败在哪一帧、哪个字段。
/// </para>
/// </summary>
/// <param name="FailedCheck">
/// 校验器给出的具体原因：<c>DidEcho</c> / <c>SessionTypeEcho</c> /
/// <c>SubFunctionEcho</c> / <c>RoutineIdEcho</c> / <c>BlockSequenceEcho</c> /
/// <c>PayloadTooShort</c> / <c>RequestTooShort</c>。
/// </param>
/// <param name="ResponseEvidenceIndex">失败响应的证据序号，可直接定位到原始帧。</param>
/// <param name="TransactionRequestEvidenceIndex">
/// 该响应所归属请求的证据序号；用于从失败明细反查是哪次请求的回显不对。
/// </param>
public sealed record UdsResponseValidationFailure(
    string FailedCheck,
    int ResponseEvidenceIndex,
    int TransactionRequestEvidenceIndex);

/// <summary>
/// Pairing output. The three failure lists are the transport-valid PDUs that
/// could not be paired or did not pass the echo checks; the report turns them
/// into noise rows with explicit reasons.
/// </summary>
public sealed record UdsTransactionBuildResult(
    IReadOnlyList<UdsOfflineTransaction> Transactions,
    IReadOnlyList<UdsOfflinePdu> OrphanResponses,
    IReadOnlyList<UdsOfflinePdu> ValidationFailures,
    IReadOnlyList<UdsOfflinePdu> DuplicateFinalResponses)
{
    public IReadOnlyList<string> FailedChecks { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 校验失败明细。与 <see cref="ValidationFailures"/> 一一对应但带原因——
    /// 前者用于噪声计数（保持既有口径），后者用于展示「哪里错了」。
    /// </summary>
    public IReadOnlyList<UdsResponseValidationFailure> ValidationFailureDetails { get; init; }
        = Array.Empty<UdsResponseValidationFailure>();
}
