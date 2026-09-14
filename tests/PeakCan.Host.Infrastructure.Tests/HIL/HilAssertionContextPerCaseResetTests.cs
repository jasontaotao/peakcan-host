using FluentAssertions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// HILAssertionContext must expose the per-case reset capability so the engine
/// can clear cumulative SecOC verdict stats between cases (spec Rev7).
/// </summary>
public class HilAssertionContextPerCaseResetTests
{
    [Fact]
    public void ResetPerCase_ClearsSecOcStats()
    {
        var stats = new SecOcStats();
        stats.Record(0x123, new VerifyResult(false, RejectReason.BadMac, 0));

        using var ctx = new HILAssertionContext(new FakeCanChannel(), new FakeDbcLookup(), secOcStats: stats);

        ((IPerCaseReset)ctx).ResetPerCase();

        stats.TryGet(0x123, out _).Should().BeFalse();
    }

    [Fact]
    public void ResetPerCase_WithoutStats_IsNoOp()
    {
        using var ctx = new HILAssertionContext(new FakeCanChannel(), new FakeDbcLookup());

        ((IPerCaseReset)ctx).ResetPerCase(); // must not throw when no stats wired
    }

    [Fact]
    public void ResetPerCase_NonResettableStats_Throws()
    {
        using var ctx = new HILAssertionContext(new FakeCanChannel(), new FakeDbcLookup(),
            secOcStats: new NonResettableStats());

        var act = () => ((IPerCaseReset)ctx).ResetPerCase();

        act.Should().Throw<InvalidOperationException>(); // fail loud, not silent no-op
    }

    [Fact]
    public void PeakCanContext_ResetPerCase_ClearsSecOcStats()
    {
        var stats = new SecOcStats();
        stats.Record(0x123, new VerifyResult(false, RejectReason.BadMac, 0));
        using var ctx = new PeakCanAssertionContext(new FakeCanChannel(), new FakeDbcLookup(), secOcStats: stats);

        ((IPerCaseReset)ctx).ResetPerCase();

        stats.TryGet(0x123, out _).Should().BeFalse();
    }

    private sealed class NonResettableStats : ISecOcStats
    {
        public bool TryGet(uint canId, out SecOcVerdictBucket bucket)
        {
            bucket = new SecOcVerdictBucket(0, 0, null);
            return false;
        }
    }
}
