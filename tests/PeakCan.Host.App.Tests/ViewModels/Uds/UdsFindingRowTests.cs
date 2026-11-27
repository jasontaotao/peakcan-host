using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// 结论行的展示纪律。
/// <para>
/// <b>核心约束：</b>倾向性判断只能以「优先排查」出现。写成「根因是」会让
/// 用户把推断当结论——而 CAN 日志只能证明 ECU 拒绝了请求，不能证明拒绝不合理。
/// </para>
/// </summary>
public sealed class UdsFindingRowTests
{
    private static readonly int[] OneObservation = { 0 };
    private static readonly string[] NoUnknowns = Array.Empty<string>();
    private static readonly string[] OneUnknown = { "无法区分是日志截断还是 ECU 未回复" };

    private static UdsFinding Finding(UdsSuspect suspect, string message = "test")
        => new(
            Code: "TEST",
            Severity: UdsFindingSeverity.Warn,
            Message: message,
            ObservedAction: UdsObservedAction.EcuRejected,
            Suspect: suspect,
            ObservationIndexes: OneObservation,
            SuggestedNextCheck: "检查一下",
            IsInference: false,
            Unknowns: NoUnknowns);

    [Theory]
    [InlineData(UdsSuspect.ClientParameters)]
    [InlineData(UdsSuspect.ClientSequencing)]
    [InlineData(UdsSuspect.EcuTiming)]
    [InlineData(UdsSuspect.EcuInternalState)]
    [InlineData(UdsSuspect.LogIntegrity)]
    public void Suspect_is_always_rendered_as_priority_language(UdsSuspect suspect)
    {
        var row = UdsFindingRow.From(Finding(suspect));

        row.SuspectText.Should().StartWith("优先排查：");
        row.SuspectText.Should().NotContain("根因");
        row.SuspectText.Should().NotContain("错误方");
        row.SuspectText.Should().NotContain("一定是");
    }

    /// <summary>
    /// 「无法判定」不能加「优先排查：」前缀——那句话读起来像给了方向，
    /// 实际什么也没说，比直说「不知道」更糟。
    /// </summary>
    [Fact]
    public void Indeterminate_does_not_pretend_to_give_a_direction()
    {
        var row = UdsFindingRow.From(Finding(UdsSuspect.Indeterminate));

        row.SuspectText.Should().Contain("无法判定");
        row.SuspectText.Should().NotStartWith("优先排查：");
    }

    [Fact]
    public void Severity_is_translated_to_chinese()
    {
        var warn = UdsFindingRow.From(Finding(UdsSuspect.ClientParameters));
        warn.Severity.Should().Be("警告");
    }

    [Fact]
    public void Unknowns_are_preserved_verbatim()
    {
        var finding = Finding(UdsSuspect.LogIntegrity) with { Unknowns = OneUnknown };

        var row = UdsFindingRow.From(finding);

        row.HasUnknowns.Should().BeTrue();
        row.Unknowns.Should().ContainSingle()
            .Which.Should().Be("无法区分是日志截断还是 ECU 未回复");
    }

    /// <summary>
    /// <b><c>EcuRespondedLate</c> 判的是 P2* 窗口，不是 P2。</b>
    /// <para>
    /// 产生点只有一个：只收到 <c>0x78 pending</c> 且日志在那之后又记了超过 P2* 的时间。
    /// P2 是「请求→首个响应」，P2* 是「0x78 之后的续期」——两回事。说成 P2 会让用户
    /// 去核一个根本不参与判定的参数。
    /// </para>
    /// </summary>
    [Fact]
    public void Responded_late_is_described_with_the_p2_star_window()
    {
        var finding = Finding(UdsSuspect.Indeterminate) with
        {
            ObservedAction = UdsObservedAction.EcuRespondedLate,
        };

        var text = UdsFindingRow.From(finding).ObservedActionText!;

        // 必须点名 P2*。老文案是「ECU 响应晚于其声明的 P2」——整句里没有「P2*」，
        // 所以这一条断言足以让回退变红（已验证）。
        text.Should().Contain("P2*");
        // 不能再出现「晚于其声明的 P2」这个说法（P2 不参与该分支的判定）。
        text.Should().NotContain("晚于其声明的 P2");
    }

    /// <summary>
    /// <b><c>ClientStopped</c> 覆盖两种相反形态，文案不能只说其中一种。</b>
    /// <para>
    /// 它既表示「没等响应就发了下一条」，也表示「干脆停发」。改前文案是
    /// 「测试仪未继续发送（未等待响应或已停止）」——前半句「未继续发送」直接
    /// 与「已发了下一条」矛盾，而那条 finding 的 Message 恰好写着「20 ms 后就发出了下一条请求」。
    /// </para>
    /// </summary>
    [Fact]
    public void Client_stopped_covers_both_not_waiting_and_stopping()
    {
        var finding = Finding(UdsSuspect.ClientSequencing) with
        {
            ObservedAction = UdsObservedAction.ClientStopped,
        };

        var text = UdsFindingRow.From(finding).ObservedActionText!;

        text.Should().NotContain("未继续发送",
            "这条动作也用于「已发了下一条」的形态，说「未继续发送」会与 Message 矛盾");
        text.Should().Contain("未等待");
    }

    [Fact]
    public void Every_suspect_has_a_distinct_description()
    {
        var texts = Enum.GetValues<UdsSuspect>()
            .Select(s => UdsFindingRow.From(Finding(s)).SuspectText)
            .ToArray();

        texts.Should().OnlyHaveUniqueItems("每个排查侧要能被区分开");
    }
}
