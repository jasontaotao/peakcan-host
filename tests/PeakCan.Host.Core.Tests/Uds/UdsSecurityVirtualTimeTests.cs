using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// M3.2（spec §5-D5）：UdsSecurity lockout 经 TimeProvider 判定——虚拟时钟
/// 推进，零真实等待。
/// </summary>
public class UdsSecurityVirtualTimeTests
{
    private static UdsSecurity NewSecurity(FakeTimeProvider clock, UdsSecurityLockoutConfig? cfg = null)
    {
        var sec = new UdsSecurity(clock);
        if (cfg is not null) sec.LockoutConfig = cfg;
        return sec;
    }

    private static void FailTimes(UdsSecurity sec, byte level, int times)
    {
        for (var i = 0; i < times; i++) sec.RecordFailedAttempt(level);
    }

    [Fact]
    public void RecordFailedAttempt_Locks_On_Virtual_Clock()
    {
        var clock = new FakeTimeProvider();
        var sec = NewSecurity(clock);

        FailTimes(sec, 1, 3);

        Assert.True(sec.IsLocked(1));
        Assert.Equal(TimeSpan.FromSeconds(5), sec.RemainingLockoutDelay(1));
    }

    [Fact]
    public void Virtual_Advance_Past_Duration_Unlocks_Without_Real_Wait()
    {
        var clock = new FakeTimeProvider();
        var sec = NewSecurity(clock);
        FailTimes(sec, 1, 3);
        Assert.True(sec.IsLocked(1));

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(sec.IsLocked(1)); // 1s remaining

        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
        Assert.False(sec.IsLocked(1));
        Assert.Equal(TimeSpan.Zero, sec.RemainingLockoutDelay(1));
    }

    [Fact]
    public void Real_Wall_Clock_Does_Not_Unlock_Virtual_Lockout()
    {
        // 虚拟时钟锁定的状态不受真实时间流逝影响（可确定性测试长锁定窗口）
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sec = NewSecurity(clock, new UdsSecurityLockoutConfig(1, TimeSpan.FromHours(1)));
        FailTimes(sec, 2, 1);

        Assert.True(sec.IsLocked(2));
        Assert.InRange(sec.RemainingLockoutDelay(2).TotalMinutes, 59.9, 60.0);
    }

    [Fact]
    public void Default_TimeProvider_Still_Wall_Clock_Compatible()
    {
        // 零破坏：缺省构造（System）下旧语义保持——锁定后配置 0 时长立即解锁
        var sec = new UdsSecurity { LockoutConfig = new UdsSecurityLockoutConfig(1, TimeSpan.Zero) };
        FailTimes(sec, 1, 1);
        Assert.False(sec.IsLocked(1));
    }
}
