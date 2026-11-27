using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// spec §8.5 最后一条：「不在表中的服务只做 SID 配对与最小长度（≥ 2）；
/// 无法校验的部分<b>明确记为「未校验」，不冒充已校验</b>」。
/// <para>
/// 改前所有服务在报告里长得一样——用户看不出 <c>0x22</c>（真的比对了 DID 回显）
/// 和 <c>0x19</c>（只看了长度）的区别，会把「长度对上了」当成「响应是对的」。
/// </para>
/// </summary>
public sealed class UdsEchoCheckLevelTests
{
    [Theory]
    [InlineData(0x10)]  // 会话类型回显
    [InlineData(0x11)]  // 复位类型回显
    [InlineData(0x19)]  // DTC 报告类型回显
    [InlineData(0x22)]  // DID 回显
    [InlineData(0x28)]  // 控制类型回显
    [InlineData(0x2E)]  // DID 回显
    [InlineData(0x27)]  // 子功能回显
    [InlineData(0x31)]  // 例程 ID 回显
    [InlineData(0x36)]  // 块序号回显
    [InlineData(0x3E)]  // 子功能回显
    [InlineData(0x85)]  // DTC 设置类型回显
    public void Services_with_full_echo_checks_are_reported_as_echo_checked(byte sid)
    {
        UdsResponseValidator.EchoCheckLevelFor(sid).Should().Be(UdsEchoCheckLevel.FullEcho);
    }

    /// <summary>
    /// 两种「只校验长度」的情形之一：服务<b>在</b> §8.5 表内，但没有可回显的字段。
    /// <para>
    /// 注意区分：<c>0x34</c> / <c>0x37</c> 在表里（长度要求明确），只是没有
    /// 可比的回显字段；它们与「表外服务」不是一回事，测试名不能混为一谈，
    /// 否则后来人会把它们当 bug 去「修」。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0x34)]  // 长度 ≥3；lengthFormatIdentifier 首版不展开
    [InlineData(0x37)]  // 长度 ≥1，合法响应只有 1 字节
    [InlineData(0x14)]
    [InlineData(0x3D)]
    public void Services_in_the_table_without_an_echo_field_are_length_only(byte sid)
    {
        UdsResponseValidator.EchoCheckLevelFor(sid).Should().Be(UdsEchoCheckLevel.LengthOnly);
    }

    /// <summary>另一种情形：服务根本不在 §8.5 表内，只做「SID 配对 + 最小长度」。</summary>
    [Theory]
    [InlineData(0x29)]  // Authentication — 有子功能但回显规则未实现
    [InlineData(0x2F)]  // InputOutputControlByIdentifier
    [InlineData(0xF0)]  // OEM 自定义
    public void Services_outside_the_echo_table_are_length_only(byte sid)
    {
        UdsResponseValidator.EchoCheckLevelFor(sid).Should().Be(UdsEchoCheckLevel.LengthOnly);
    }

    /// <summary>
    /// 负响应（0x7F）长度恒为 3，由配对器直接判定，不走正响应回显表。
    /// </summary>
    [Fact]
    public void Negative_response_sid_is_not_part_of_the_positive_echo_table()
    {
        UdsResponseValidator.EchoCheckLevelFor(0x7F).Should().Be(UdsEchoCheckLevel.LengthOnly);
    }

    /// <summary>
    /// 正响应事务：等级由请求 SID 查表得出。
    /// </summary>
    [Fact]
    public void Positive_transaction_reports_the_level_looked_up_from_its_request_sid()
    {
        Build(UdsFinalKind.Positive, 0x22, 0xF1, 0x90).EchoCheck
            .Should().Be(UdsEchoCheckLevel.FullEcho);
        Build(UdsFinalKind.Positive, 0xF0, 0x02, 0xFF).EchoCheck
            .Should().Be(UdsEchoCheckLevel.LengthOnly);
    }

    /// <summary>
    /// <b>负响应不能借用请求 SID 的校验强度。</b>
    /// <c>0x2E F190 → 7F 2E 33</c> 在配对器里只校验「长度恰为 3」，
    /// 从不调用 <see cref="UdsResponseValidator.TryValidate"/>。
    /// 标成「已校验回显」等于宣称做过 DID 比对——恰好是这次改动要消灭的
    /// 「冒充已校验」，而且发生在用户最需要看失败原因的事务上。
    /// </summary>
    [Fact]
    public void Negative_response_is_not_reported_as_echo_checked()
    {
        var tx = Build(UdsFinalKind.Negative, 0x2E, 0xF1, 0x90, 0x01) with { FinalNrc = 0x33 };

        tx.EchoCheck.Should().Be(UdsEchoCheckLevel.NotChecked);
        tx.EchoCheckDisplay.Should().NotContain("已校验回显");
    }

    /// <summary>未闭合事务根本没有最终响应，谈不上校验。</summary>
    [Fact]
    public void Unclosed_transaction_is_not_reported_as_echo_checked()
    {
        var tx = Build(UdsFinalKind.None, 0x22, 0xF1, 0x90);

        tx.EchoCheck.Should().Be(UdsEchoCheckLevel.NotChecked);
        tx.EchoCheckDisplay.Should().Contain("未校验");
    }

    /// <summary>三种强度的文案必须互不相同——否则「已校验」和「未校验」会撞车。</summary>
    [Fact]
    public void The_three_display_strings_are_distinct()
    {
        var full = Build(UdsFinalKind.Positive, 0x22, 0xF1, 0x90).EchoCheckDisplay;
        var lengthOnly = Build(UdsFinalKind.Positive, 0xF0, 0x02).EchoCheckDisplay;
        var none = Build(UdsFinalKind.None, 0x22, 0xF1, 0x90).EchoCheckDisplay;

        new[] { full, lengthOnly, none }.Should().OnlyHaveUniqueItems();
    }

    private static readonly int[] FirstEvidence = { 0 };

    private static UdsOfflineTransaction Build(UdsFinalKind final, params byte[] requestPayload)
        => new(
            Request: new UdsOfflinePdu(UdsDirectionRole.Request, requestPayload,
                0.0, 0.0, 0, 0, FirstEvidence, false, true, Array.Empty<string>()),
            Responses: Array.Empty<UdsOfflinePdu>(),
            FinalKind: final,
            PendingCount: 0,
            FinalNrc: null,
            IsOrphan: false,
            IsAmbiguous: false,
            HasDuplicateFinalResponse: false,
            Unknowns: Array.Empty<string>());

    /// <summary>
    /// 抑制了正响应的请求没有响应是规范要求，文案不能说成「未校验（无最终正响应）」——
    /// 那读起来像出了问题。
    /// </summary>
    [Fact]
    public void A_suppressed_transaction_says_it_needs_no_verification()
    {
        var tx = new UdsTransactionBuilder()
            .Build(new[]
            {
                new UdsOfflinePdu(UdsDirectionRole.Request, new byte[] { 0x3E, 0x80 },
                    0.0, 0.0, 0, 0, FirstEvidence, false, true, Array.Empty<string>()),
            })
            .Transactions[0];

        tx.FinalKind.Should().Be(UdsFinalKind.Suppressed);
        tx.EchoCheckDisplay.Should().Contain("无需校验");
        tx.EchoCheckDisplay.Should().NotContain("无最终正响应");
    }
}
