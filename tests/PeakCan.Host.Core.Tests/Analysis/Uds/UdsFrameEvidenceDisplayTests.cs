using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// spec §6.2 要求证据行保留「日志原始方向」和「最终分类桶（噪声帧附带原因）」。
/// <para>
/// 改前 <see cref="UdsFrameEvidence.Display"/> 只有
/// <c>序号 时间 ch 帧类型 hex</c>——用户看 hex 分不出哪条是请求、哪条是响应，
/// 也看不出这条为什么进了噪声桶。
/// </para>
/// </summary>
public sealed class UdsFrameEvidenceDisplayTests
{
    private static UdsFrameEvidence Evidence(
        bool? isTransmit, UdsFrameClass cls = UdsFrameClass.MatchedDiagnostic,
        UdsNoiseReason? reason = null)
        => new(7, 1.234, 1, 0x7E0, false, isTransmit, cls, reason,
            "SingleFrame", 4, new byte[] { 0x03, 0x22, 0xF1, 0x90 });

    [Theory]
    [InlineData(true, "TX")]
    [InlineData(false, "RX")]
    public void Display_shows_the_recorded_direction(bool isTransmit, string expected)
    {
        Evidence(isTransmit).Display.Should().Contain(expected);
    }

    /// <summary>
    /// 方向未知不能猜成 TX 或 RX——「没标」和「标了 TX」是不同的事实。
    /// </summary>
    [Fact]
    public void Unknown_direction_is_not_guessed()
    {
        var row = Evidence(isTransmit: null);
        row.DirectionText.Should().Be("—");
        row.Display.Should().NotContain("TX").And.NotContain("RX");
    }

    [Fact]
    public void Noise_frame_shows_its_reason_in_the_display_line()
    {
        var row = Evidence(true, UdsFrameClass.Noise, UdsNoiseReason.OrphanConsecutiveFrame);
        row.Display.Should().Contain("噪声");
        row.Display.Should().Contain("OrphanConsecutiveFrame");
    }

    [Fact]
    public void Matched_frame_is_labelled_as_diagnostic()
    {
        Evidence(true).Display.Should().Contain("诊断");
    }

    [Fact]
    public void Unsupported_input_is_labelled()
    {
        Evidence(true, UdsFrameClass.UnsupportedInput).Display.Should().Contain("未支持");
    }

    [Fact]
    public void Display_still_carries_index_time_channel_and_id()
    {
        var display = Evidence(true).Display;
        display.Should().Contain("7");
        display.Should().Contain("1.234");
        display.Should().Contain("ch1");
        display.Should().Contain("0x7E0");
        display.Should().Contain("0322F190");
    }
}
