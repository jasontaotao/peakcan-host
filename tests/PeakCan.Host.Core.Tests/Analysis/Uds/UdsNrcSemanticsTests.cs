using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// NRC 到「优先排查侧」的映射。
/// <para>
/// <b>这是客户端策略，不是协议判定。</b>协议只规定 NRC 的含义，不规定「谁该去查」。
/// 因此每条映射都必须能被工程师反驳并修改，而不是当成规范。
/// </para>
/// <para>
/// <b>严禁反向使用：</b>不能因为 NRC 是 0x35 就在 UI 上写「根因：key 算错」。
/// CAN 日志只能证明 ECU 拒绝了 key，不能证明算法写错——也可能是 seed 用错、
/// 安全等级不符或 ECU 内部状态。UI 文案必须是「优先排查」。
/// </para>
/// </summary>
public sealed class UdsNrcSemanticsTests
{
    [Theory]
    [InlineData(0x10, "GeneralReject", UdsSuspect.Indeterminate)]
    [InlineData(0x11, "ServiceNotSupported", UdsSuspect.ClientParameters)]
    [InlineData(0x12, "SubFunctionNotSupported", UdsSuspect.ClientParameters)]
    [InlineData(0x13, "IncorrectMessageLength", UdsSuspect.ClientParameters)]
    [InlineData(0x22, "ConditionsNotCorrect", UdsSuspect.EcuInternalState)]
    [InlineData(0x24, "RequestSequenceError", UdsSuspect.ClientSequencing)]
    [InlineData(0x31, "RequestOutOfRange", UdsSuspect.ClientParameters)]
    [InlineData(0x33, "SecurityAccessDenied", UdsSuspect.ClientSequencing)]
    [InlineData(0x35, "InvalidKey", UdsSuspect.ClientParameters)]
    [InlineData(0x36, "ExceededNumberOfAttempts", UdsSuspect.ClientParameters)]
    [InlineData(0x37, "RequiredTimeDelayNotExpired", UdsSuspect.ClientSequencing)]
    [InlineData(0x70, "UploadDownloadNotAccepted", UdsSuspect.ClientParameters)]
    [InlineData(0x72, "GeneralProgrammingFailure", UdsSuspect.EcuInternalState)]
    [InlineData(0x73, "WrongBlockSequenceCounter", UdsSuspect.ClientParameters)]
    [InlineData(0x7E, "SubFunctionNotSupportedInActiveSession", UdsSuspect.ClientSequencing)]
    [InlineData(0x7F, "ServiceNotSupportedInActiveSession", UdsSuspect.ClientSequencing)]
    public void Known_nrc_maps_to_name_and_suspect_side(byte nrc, string name, UdsSuspect suspect)
    {
        var semantics = UdsNrcSemantics.Describe(nrc);

        semantics.Name.Should().Be(name);
        semantics.Suspect.Should().Be(suspect);
    }

    /// <summary>
    /// 未枚举的 NRC（OEM 范围）必须明确「无法判定」，不能借用相近名称。
    /// </summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x50)]
    [InlineData(0xA0)]
    [InlineData(0xFF)]
    public void Unknown_nrc_is_indeterminate_and_keeps_hex(byte nrc)
    {
        var semantics = UdsNrcSemantics.Describe(nrc);

        semantics.Name.Should().BeNull("未知值不能借用相近名称");
        semantics.Suspect.Should().Be(UdsSuspect.Indeterminate);
        semantics.Display.Should().Contain(nrc.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Pending_nrc_is_not_a_failure_side()
    {
        // 0x78 是「还在处理」，不是失败。若把它映射成某个排查侧，
        // UI 会把正常的擦除等待显示成异常。
        var semantics = UdsNrcSemantics.Describe(0x78);

        semantics.Suspect.Should().Be(UdsSuspect.Indeterminate);
    }

    [Fact]
    public void Every_enum_member_has_a_mapping()
    {
        // 枚举加了新成员却忘了加映射，会让 Describe 静默回落到 Indeterminate，
        // 看起来「无法判定」实际是「忘了配」。
        foreach (UdsNegativeResponseCode value in Enum.GetValues<UdsNegativeResponseCode>())
        {
            var semantics = UdsNrcSemantics.Describe((byte)value);
            semantics.Name.Should().NotBeNull($"NRC 0x{(byte)value:X2} 已在枚举里，必须有名称");
        }
    }

    [Fact]
    public void Display_is_chinese_and_includes_both_name_and_hex()
    {
        var semantics = UdsNrcSemantics.Describe(0x33);

        semantics.Display.Should().Contain("SecurityAccessDenied");
        semantics.Display.Should().Contain("0x33");
        semantics.Display.Should().Contain("安全", "给用户看的中文说明");
    }

    /// <summary>
    /// <b>文案纪律（设计文档 §8.8）：</b><see cref="UdsNrcDescription.Display"/>
    /// 会被拼进 finding 的事实行 <c>Message</c>，因此只能陈述协议含义，
    /// 不能包含「通常是客户端的错」这类方向性判断——那属于「优先排查」，
    /// 由 <see cref="UdsNrcDescription.Suspect"/> 单独渲染。
    /// <para>
    /// 改前 0x11 / 0x24 / 0x31 / 0x36 / 0x72 的说明里写了「通常是……」，
    /// 于是同一条 finding 里同时出现「事实：通常是客户端发了错误的服务」
    /// 和「优先排查：客户端参数」——用户读到的仍然是判责。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0x11)]
    [InlineData(0x24)]
    [InlineData(0x31)]
    [InlineData(0x36)]
    [InlineData(0x72)]
    [InlineData(0x35)]
    [InlineData(0x33)]
    public void Display_carries_no_directional_judgement(byte nrc)
    {
        var display = UdsNrcSemantics.Describe(nrc).Display;

        display.Should().NotContain("通常是");
        display.Should().NotContain("客户端的错");
        display.Should().NotContain("客户端的");
        display.Should().NotContain("优先");
        display.Should().NotContain("根因");
    }

    /// <summary>
    /// 方向性提示不能丢——它应该出现在 <c>NextCheck</c> 里，
    /// 由 UI 渲染成「下一步：…」。
    /// </summary>
    [Fact]
    public void Directional_hint_moves_to_NextCheck()
    {
        var semantics = UdsNrcSemantics.Describe(0x33);

        semantics.NextCheck.Should().NotBeNull();
        semantics.NextCheck.Should().Contain("0x27", "0x33 要先查前置解锁");
    }

    [Fact]
    public void Indeterminate_nrcs_may_have_no_next_check()
    {
        // 协议没定义具体条件时，硬给检查建议等于猜。
        UdsNrcSemantics.Describe(0x10).NextCheck.Should().BeNull();
        UdsNrcSemantics.Describe(0x78).NextCheck.Should().BeNull();
    }

    /// <summary>
    /// 事实行与建议行必须来自不同字段，不能是同一段文字拆两半。
    /// </summary>
    [Fact]
    public void Display_and_NextCheck_are_independent()
    {
        foreach (var nrc in new byte[] { 0x11, 0x24, 0x31, 0x33, 0x35, 0x36, 0x72, 0x73 })
        {
            var d = UdsNrcSemantics.Describe(nrc);
            if (d.NextCheck is null) continue;
            d.Display.Should().NotContain(d.NextCheck);
        }
    }

    /// <summary>
    /// <b>每个动作值都必须有文案。</b>漏一个会让 UI 与导出同时显示空白，
    /// 而「直接动作」正是用户问「错误方是谁」时看的那一行。
    /// </summary>
    [Fact]
    public void Every_observed_action_has_a_description()
    {
        var missing = Enum.GetValues<UdsObservedAction>()
            .Where(a => string.IsNullOrWhiteSpace(UdsNrcSemantics.DescribeAction(a)))
            .ToArray();

        missing.Should().BeEmpty("这些动作值没有中文文案，UI 与导出会显示空白");
    }

    /// <summary>
    /// <c>EcuRespondedLate</c> 与 <c>EcuStillProcessing</c> 必须说得不一样——
    /// 前者是「超了 P2*」，后者是「还在等，没超」。文案一样就等于把两件事混成一件事。
    /// </summary>
    [Fact]
    public void Responded_late_and_still_processing_are_worded_differently()
    {
        var late = UdsNrcSemantics.DescribeAction(UdsObservedAction.EcuRespondedLate);
        var processing = UdsNrcSemantics.DescribeAction(UdsObservedAction.EcuStillProcessing);

        late.Should().NotBe(processing);
        late.Should().Contain("超过");
        processing.Should().NotContain("超过");
    }
}
