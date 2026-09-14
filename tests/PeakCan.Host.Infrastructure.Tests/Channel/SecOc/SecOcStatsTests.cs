using FluentAssertions;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Tests.Channel.SecOc;

/// <summary>
/// Per-case reset contract for SecOC verdict statistics (spec Rev7): the HIL
/// engine clears the per-canId buckets at each case start so an earlier case's
/// rejection cannot leak into the next case's <c>secocRejected(id)</c> assertion
/// (attack-suite false-pass fix).
/// </summary>
public class SecOcStatsTests
{
    private static VerifyResult Rejected(RejectReason reason = RejectReason.BadMac) => new(false, reason, 0);
    private static VerifyResult Accepted(uint fv = 1) => new(true, null, fv);

    [Fact]
    public void Reset_ClearsAllBuckets()
    {
        var stats = new SecOcStats();
        stats.Record(0x123, Rejected());
        stats.Record(0x456, Accepted());

        stats.ResetPerCase();

        stats.TryGet(0x123, out _).Should().BeFalse();
        stats.TryGet(0x456, out _).Should().BeFalse();
        stats.TotalAccepted.Should().Be(0);
        stats.TotalRejected.Should().Be(0);
    }

    [Fact]
    public void Reset_ThenRecord_OnlyCountsPostResetFrames()
    {
        var stats = new SecOcStats();
        stats.Record(0x123, Rejected()); // pre-reset rejection must not leak forward
        stats.ResetPerCase();
        stats.Record(0x123, Accepted());

        stats.TryGet(0x123, out var bucket).Should().BeTrue();
        bucket.Accepted.Should().Be(1);
        bucket.Rejected.Should().Be(0);
        bucket.LastReason.Should().BeNull();
    }
}
