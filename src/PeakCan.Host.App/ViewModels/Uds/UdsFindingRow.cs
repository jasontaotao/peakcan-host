using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 一条结论的展示形态。把 Core 的枚举翻译成中文，**并强制文案纪律**：
/// 倾向性判断只以「优先排查」的形式出现，绝不写成「根因是」。
/// </summary>
public sealed class UdsFindingRow
{
    private UdsFindingRow(UdsFinding source, string suspectText, int? transactionNumber)
    {
        Source = source;
        SuspectText = suspectText;
        TransactionNumber = transactionNumber;
        BodyText = StripNumberPrefix(source.Message, transactionNumber);
    }

    public UdsFinding Source { get; }

    /// <summary>
    /// 这条结论对应的事务序号（与事务表首列、<see cref="UdsOfflineTransaction.Index"/> 同源）。
    /// <para>
    /// <b>它不是装饰：</b>结论里的 <c>#N</c> 原来是一个界面上根本不存在的编号——
    /// 事务表没有序号列，用户数到第 N 行也无从下手，一筛选更是彻底错位。
    /// 现在这个号既显示在表里，也用来一键定位。
    /// </para>
    /// <para><c>null</c> = 该结论没有归属事务（例如刷写流程终止于「该步骤未出现」）。</para>
    /// </summary>
    public int? TransactionNumber { get; }

    /// <summary>展示用的 <c>#N</c> 徽标；没有归属事务时为 null（按钮隐藏）。</summary>
    public string? NumberText => TransactionNumber is { } n ? $"#{n}" : null;

    public string Severity => Source.Severity switch
    {
        UdsFindingSeverity.Error => "错误",
        UdsFindingSeverity.Warn => "警告",
        _ => "提示",
    };

    /// <summary>
    /// 结论文案，<b>去掉开头的 <c>#N</c> 前缀</b>——号码改由可点击徽标显示，
    /// 同一行里不再出现两次。导出报告与 AI prompt 仍用 Core 原文（含 <c>#N</c>）。
    /// </summary>
    public string BodyText { get; }

    /// <summary>
    /// <b>文案固定为「优先排查：…」。</b>不能写成「根因」或「问题出在」——
    /// CAN 日志只能证明 ECU 拒绝了请求，不能证明拒绝不合理。
    /// </summary>
    public string SuspectText { get; }

    public string? SuggestedNextCheck => Source.SuggestedNextCheck;

    /// <summary>
    /// 本条是推断而非直接观测。刷写 finding 里「该步骤未出现在日志中」就是推断，
    /// UI 必须标出来，否则它看起来和实测结论一样确定。
    /// </summary>
    public bool IsInference => Source.IsInference;

    /// <summary>
    /// 日志直接观测到的动作方。用户问的「错误方是谁」由这一行回答——
    /// 但只陈述「ECU 拒绝了请求」这个事实，不评价拒绝是否合理。
    /// </summary>
    public string? ObservedActionText => UdsNrcSemantics.DescribeAction(Source.ObservedAction);

    /// <summary>不可知项，一行一条。</summary>
    public IReadOnlyList<string> Unknowns => Source.Unknowns;

    public bool HasUnknowns => Source.Unknowns.Count > 0;

    /// <summary>没有报告上下文时（单测 / 预览）构造：不带事务号，也不剥前缀。</summary>
    public static UdsFindingRow From(UdsFinding finding) => From(finding, report: null);

    public static UdsFindingRow From(UdsFinding finding, UdsOfflineReport? report)
    {
        ArgumentNullException.ThrowIfNull(finding);

        // 映射下沉到 Core：UI / 导出 / AI 提示词共用一份，避免文案漂移。
        // Indeterminate 不加「优先排查：」前缀——那句话读起来像给了方向，
        // 实际什么也没说。
        var text = finding.Suspect == UdsSuspect.Indeterminate
            ? "无法判定（需更多证据）"
            : "优先排查：" + UdsNrcSemantics.DescribeSuspect(finding.Suspect);

        return new(finding, text, ResolveTransactionNumber(finding, report));
    }

    /// <summary>
    /// 经观测事实反查事务号：<c>finding → ObservationIndexes → 观测.TransactionIndex → 事务.Index</c>。
    /// <para>
    /// <b>不解析结论文案里的 <c>#N</c></b>：文案是给人看的，改一个字就会让定位静默失效。
    /// </para>
    /// </summary>
    private static int? ResolveTransactionNumber(UdsFinding finding, UdsOfflineReport? report)
    {
        if (report is null) return null;

        foreach (var observationIndex in finding.ObservationIndexes)
        {
            if (observationIndex < 0 || observationIndex >= report.Observations.Count) continue;
            if (report.Observations[observationIndex].TransactionIndex is not { } txIndex) continue;
            if (txIndex < 0 || txIndex >= report.Transactions.Count) continue;

            var transaction = report.Transactions[txIndex];
            return transaction.Index > 0 ? transaction.Index : txIndex + 1;
        }

        return null;
    }

    /// <summary>
    /// 去掉文案开头的 <c>#N </c>（仅当它确实与本条事务号一致时）。
    /// 不一致就不动——宁可露出原文，也不要把两个不同的号悄悄合谋成一个。
    /// </summary>
    private static string StripNumberPrefix(string message, int? transactionNumber)
    {
        if (transactionNumber is not { } number) return message;

        var prefix = $"#{number} ";
        return message.StartsWith(prefix, StringComparison.Ordinal)
            ? message[prefix.Length..]
            : message;
    }
}
