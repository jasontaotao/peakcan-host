using FluentAssertions;
using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public sealed class UdsSecurityTests
{
    [Fact]
    public void IsLocked_Returns_False_When_Not_Locked()
    {
        var sut = new UdsSecurity();
        sut.IsLocked(level: 0x01).Should().BeFalse(
            "a fresh UdsSecurity must not be locked for any level");
    }

    [Fact]
    public void RecordFailedAttempt_BelowThreshold_DoesNotLock()
    {
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 3, TimeSpan.FromSeconds(5)) };

        sut.RecordFailedAttempt(level: 0x01);
        sut.RecordFailedAttempt(level: 0x01);

        sut.IsLocked(0x01).Should().BeFalse(
            "2 attempts is below the 3-attempt threshold, no lockout yet");
    }

    [Fact]
    public void RecordFailedAttempt_ReachesThreshold_LocksLevel()
    {
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 3, TimeSpan.FromSeconds(5)) };

        sut.RecordFailedAttempt(level: 0x01);
        sut.RecordFailedAttempt(level: 0x01);
        sut.RecordFailedAttempt(level: 0x01);  // 3rd attempt hits threshold

        sut.IsLocked(0x01).Should().BeTrue(
            "3rd attempt must trigger lockout per MaxAttempts=3 policy");
    }

    [Fact]
    public void IsLocked_Expires_AfterLockoutDuration()
    {
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 1, TimeSpan.FromMilliseconds(50)) };

        sut.RecordFailedAttempt(level: 0x01);
        sut.IsLocked(0x01).Should().BeTrue("just locked, lockout is active");

        Thread.Sleep(100);
        sut.IsLocked(0x01).Should().BeFalse(
            "lockout duration (50 ms) has elapsed, level is unlocked again");
    }

    [Fact]
    public void RemainingLockoutDelay_Decreases_OverTime()
    {
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 1, TimeSpan.FromMilliseconds(200)) };

        sut.RecordFailedAttempt(level: 0x01);
        var first = sut.RemainingLockoutDelay(0x01);
        Thread.Sleep(50);
        var second = sut.RemainingLockoutDelay(0x01);

        second.Should().BeLessThan(first,
            "remaining lockout delay must monotonically decrease as wall-clock advances");
    }

    [Fact]
    public void ResetLockout_Clears_AttemptCount_And_LockedUntilUtc()
    {
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 1, TimeSpan.FromSeconds(5)) };

        sut.RecordFailedAttempt(level: 0x01);  // locks
        sut.IsLocked(0x01).Should().BeTrue();

        sut.ResetLockout(0x01);

        sut.IsLocked(0x01).Should().BeFalse(
            "ResetLockout clears the lockout window");
        // Subsequent attempt should re-trigger (because counter also reset)
        sut.RecordFailedAttempt(level: 0x01);
        sut.IsLocked(0x01).Should().BeTrue(
            "after ResetLockout, the next failed attempt re-triggers lockout from threshold=1");
    }

    [Fact]
    public void DifferentLevels_Have_Independent_Counters()
    {
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 2, TimeSpan.FromSeconds(5)) };

        sut.RecordFailedAttempt(level: 0x01);
        sut.RecordFailedAttempt(level: 0x01);  // 0x01 locked
        sut.RecordFailedAttempt(level: 0x03);  // 0x03 first attempt, not locked

        sut.IsLocked(0x01).Should().BeTrue("0x01 hit MaxAttempts threshold");
        sut.IsLocked(0x03).Should().BeFalse("0x03 has only 1 attempt, below threshold");

        sut.Reset();  // session change — clears auth but preserves lockout (D8)
        sut.IsLocked(0x01).Should().BeTrue(
            "lockout state must survive session reset (D8 — security policy independent of session)");
        sut.IsLocked(0x03).Should().BeFalse(
            "0x03 was not locked before reset, still not locked after");
    }
}