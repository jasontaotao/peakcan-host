using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 一条结论的展示形态。把 Core 的枚举翻译成中文，**并强制文案纪律**：
/// 倾向性判断只以「优先排查」的形式出现，绝不写成「根因是」。
/// </summary>
public sealed class UdsFindingRow
{
    private UdsFindingRow(UdsFinding source, string suspectText)
    {
        Source = source;
        SuspectText = suspectText;
    }

    public UdsFinding Source { get; }

    public string Severity => Source.Severity switch
    {
        UdsFindingSeverity.Error => "错误",
        UdsFindingSeverity.Warn => "警告",
        _ => "提示",
    };

    public string Message => Source.Message;

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

    /// <summary>观测事实可点击定位到原始证据。</summary>
    public IReadOnlyList<int> ObservationIndexes => Source.ObservationIndexes;

    public static UdsFindingRow From(UdsFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        // 映射下沉到 Core：UI / 导出 / AI 提示词共用一份，避免文案漂移。
        // Indeterminate 不加「优先排查：」前缀——那句话读起来像给了方向，
        // 实际什么也没说。
        var text = finding.Suspect == UdsSuspect.Indeterminate
            ? "无法判定（需更多证据）"
            : "优先排查：" + UdsNrcSemantics.DescribeSuspect(finding.Suspect);

        return new(finding, text);
    }
}
