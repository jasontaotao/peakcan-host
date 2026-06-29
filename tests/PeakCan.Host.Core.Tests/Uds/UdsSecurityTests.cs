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

    // ---------- v1.4.2 PATCH Item 1: SetSeed preserves lockout state (D8) ----------

    /// <summary>
    /// v1.4.2 PATCH: SetSeed on a level that has already had a failed
    /// attempt must preserve the existing lockout state. Aligned to the
    /// D8 invariant that lockout is independent of session state — the
    /// same pattern as <see cref="UdsSecurity.Reset"/>.
    /// <para>
    /// RED-then-GREEN: this test FAILS on v1.4.1-shipped code (SetSeed
    /// creates fresh <c>SecurityLevelState</c>, wiping <c>AttemptCount</c>
    /// and clearing <c>LockedUntilUtc</c>). It PASSES after the v1.4.2
    /// PATCH fix mutates the existing state in place.
    /// </para>
    /// </summary>
    [Fact]
    public void SetSeed_PreservesLockoutState_OnExistingLevel()
    {
        // Arrange — MaxAttempts=1, lockout already triggered
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 1, TimeSpan.FromSeconds(5)) };

        sut.RecordFailedAttempt(level: 0x01);  // locks the level
        sut.IsLocked(0x01).Should().BeTrue("just locked, lockout is active");

        // Act — SetSeed on the locked level
        sut.SetSeed(0x01, new byte[] { 0xAA, 0xBB });

        // Assert — lockout state preserved (the bug was: SetSeed wiped
        //   AttemptCount + LockedUntilUtc, defeating concurrent-access
        //   lockout)
        sut.IsLocked(0x01).Should().BeTrue(
            "SetSeed must preserve existing lockout state per D8 invariant (align to Reset() behavior)");
        sut.RemainingLockoutDelay(0x01).Should().BeGreaterThan(TimeSpan.Zero,
            "lockout window must still be active after SetSeed");
    }

    /// <summary>
    /// v1.4.2 PATCH: SetSeed on a never-seen level creates fresh state.
    /// Sanity check that the else-branch (first observation) still works
    /// after the mutate-in-place fix for the if-branch.
    /// </summary>
    [Fact]
    public void SetSeed_OnNewLevel_CreatesFreshState()
    {
        // Arrange — fresh UdsSecurity, no prior state for level 0x03
        var sut = new UdsSecurity { LockoutConfig = new(MaxAttempts: 1, TimeSpan.FromSeconds(5)) };

        // Act — SetSeed on a never-seen level
        sut.SetSeed(0x03, new byte[] { 0xCC, 0xDD });

        // Assert — fresh state: seed stored, no lockout (else-branch)
        sut.GetSeed(0x03).Should().BeEquivalentTo(new byte[] { 0xCC, 0xDD },
            "SetSeed on new level creates fresh state with the provided seed");
        sut.IsLocked(0x03).Should().BeFalse(
            "fresh state has no lockout");
        sut.IsAuthenticated(0x03).Should().BeFalse(
            "SetSeed marks level as not yet authenticated");
    }
}