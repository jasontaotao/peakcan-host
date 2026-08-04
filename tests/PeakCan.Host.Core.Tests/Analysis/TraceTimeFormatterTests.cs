using FluentAssertions;
using PeakCan.HIL.Core.Analysis;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Analysis;

/// <summary>
/// TraceTimeFormatter 单元测试 - 验证统一秒数 F4 格式。
/// 该格式化器是图表 X 轴 / AI chat system prompt / 工具 *_label 三路的
/// 唯一真相源，任何回归都会导致用户看到的时间与 AI 描述不一致。
/// </summary>
public class TraceTimeFormatterTests
{
    [Fact]
    public void Format_ReturnsSecondsWithFourDecimalPlaces()
    {
        TraceTimeFormatter.Format(158340.5101, null).Should().Be("158340.5101");
    }

    [Fact]
    public void Format_Zero_ReturnsZeroWithFourDecimals()
    {
        TraceTimeFormatter.Format(0, null).Should().Be("0.0000");
    }

    [Fact]
    public void Format_PadsTrailingZeros()
    {
        // 63.5 -> "63.5000" (F4 补零到4位)
        TraceTimeFormatter.Format(63.5, null).Should().Be("63.5000");
    }

    [Fact]
    public void Format_RoundsMoreThanFourDecimals()
    {
        // 3661.123456 -> F4 截断到 "3661.1235"
        TraceTimeFormatter.Format(3661.123456, null).Should().Be("3661.1235");
    }

    [Fact]
    public void Format_IgnoresWallClockOrigin()
    {
        // WallClockOrigin 不影响输出 - 统一用秒数
        var origin = new DateTime(2026, 7, 1, 8, 32, 1);
        TraceTimeFormatter.Format(3661, origin).Should().Be("3661.0000");
        TraceTimeFormatter.Format(3661, null).Should().Be("3661.0000");
    }
}
