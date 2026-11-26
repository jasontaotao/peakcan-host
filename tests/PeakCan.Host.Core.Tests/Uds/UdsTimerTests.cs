using FluentAssertions;
using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// <see cref="UdsTimer.ResetToDefaults"/> 是「断连复位」的唯一默认值来源。
/// <para>
/// <see cref="UdsClient.ResetDiagnosticState"/> 先复位 timer，再用 timer 的值复位
/// <see cref="UdsSession"/>——这样 Session 不必自己再维护一份写死的 50/5000。
/// 两份默认值并存正是本阶段要消灭的问题：显示说复位了、实际生效的还是上一台
/// ECU 协商的值。
/// </para>
/// </summary>
public sealed class UdsTimerTests
{
    [Fact]
    public void ResetToDefaults_Restores_Construction_Defaults()
    {
        var timer = new UdsTimer();
        timer.P2Timeout = TimeSpan.FromMilliseconds(1234);
        timer.P2StarTimeout = TimeSpan.FromMilliseconds(5678);

        timer.ResetToDefaults();

        timer.P2Timeout.Should().Be(UdsTimer.DefaultP2Timeout);
        timer.P2StarTimeout.Should().Be(UdsTimer.DefaultP2StarTimeout);
        timer.P2Timeout.Should().Be(TimeSpan.FromMilliseconds(50), "P2 默认 50 ms");
        timer.P2StarTimeout.Should().Be(TimeSpan.FromMilliseconds(5000), "P2* 默认 5000 ms");
    }

    [Fact]
    public void Default_Constants_Match_The_Property_Initializers()
    {
        // 常量与属性初值必须是同一个来源，否则「复位到默认」和「新建一个」会分叉。
        var fresh = new UdsTimer();

        fresh.P2Timeout.Should().Be(UdsTimer.DefaultP2Timeout);
        fresh.P2StarTimeout.Should().Be(UdsTimer.DefaultP2StarTimeout);
    }
}
