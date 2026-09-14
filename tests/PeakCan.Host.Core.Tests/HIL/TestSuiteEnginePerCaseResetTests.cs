using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL;

/// <summary>
/// The engine must invoke the per-case reset capability exactly once per case,
/// before case setup, so cumulative subsystem state (SecOC verdict stats) cannot
/// leak across cases (spec Rev7). Rev8 additionally makes the contract structural:
/// a context that exposes SecOC stats without implementing IPerCaseReset must fail
/// loudly instead of silently skipping the reset.
/// </summary>
public class TestSuiteEnginePerCaseResetTests
{
    private static TestSuite TwoCaseSuite()
    {
        TestCase Make(string name) => new(
            Id: $"case_{name}", Name: name, Description: "",
            PreConditions: null, Steps: new[] { TestCaseStep.Create(new CommentStep("doc")) },
            PostConditions: null, Tags: Array.Empty<string>(), TimeoutMs: 0, CaseFixtureKeys: null);
        return new TestSuite("S", new[] { Make("a"), Make("b") },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);
    }

    private static TestSuite OneCaseWithFixture(string fixtureKey)
    {
        var testCase = new TestCase(
            Id: "case_fx", Name: "fx", Description: "",
            PreConditions: null, Steps: new[] { TestCaseStep.Create(new CommentStep("doc")) },
            PostConditions: null, Tags: Array.Empty<string>(), TimeoutMs: 0,
            CaseFixtureKeys: new[] { fixtureKey });
        return new TestSuite("S", new[] { testCase },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);
    }

    [Fact]
    public async Task ExecuteAsync_CallsResetPerCase_OncePerCase()
    {
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), Array.Empty<IStepExecutor>());
        var ctx = new ResetCountingContext();

        var result = await engine.ExecuteAsync(TwoCaseSuite(), ctx, new TestSuiteConfig());

        Assert.Equal(2, result.CaseResults.Count);
        Assert.Equal(2, ctx.ResetCount);
    }

    [Fact]
    public async Task ExecuteAsync_ResetPerCase_RunsBeforeCaseSetup()
    {
        var resolver = new FakeFixtureResolver();
        var fixture = new ProbeFixture();
        resolver.Register("fx", fixture);
        var engine = new TestSuiteEngine(resolver, Array.Empty<IStepExecutor>());
        var ctx = new ResetCountingContext();

        await engine.ExecuteAsync(OneCaseWithFixture("fx"), ctx, new TestSuiteConfig());

        Assert.Equal(1, fixture.SetupCallCount);
        Assert.Equal(1, ctx.ResetCount);
        // Setup 观测到的 ResetCount 必须 ≥1：证明 reset 在 fixture.SetupAsync 之前已执行。
        Assert.Equal(1, ctx.ResetCountAtSetup);
    }

    [Fact]
    public async Task ExecuteAsync_SecOcStatsSourceWithoutReset_Throws()
    {
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), Array.Empty<IStepExecutor>());
        var ctx = new StatsOnlyContext();

        var act = () => engine.ExecuteAsync(TwoCaseSuite(), ctx, new TestSuiteConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    private sealed class ResetCountingContext : IAssertionContext, IPerCaseReset
    {
        public int ResetCount;
        public int ResetCountAtSetup = -1;
        public void ResetPerCase() => ResetCount++;

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }

    /// <summary>Exposes SecOC stats but deliberately omits IPerCaseReset (Rev8 enforcement case).</summary>
    private sealed class StatsOnlyContext : IAssertionContext, ISecOcStatsSource
    {
        public ISecOcStats? SecOcStats { get; } = new EmptyStats();

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();

        private sealed class EmptyStats : ISecOcStats
        {
            public bool TryGet(uint canId, out SecOcVerdictBucket bucket)
            {
                bucket = new SecOcVerdictBucket(0, 0, null);
                return false;
            }
        }
    }

    private sealed class ProbeFixture : ITestFixture
    {
        public int SetupCallCount { get; private set; }

        public Task SetupAsync(IAssertionContext ctx, CancellationToken ct)
        {
            SetupCallCount++;
            if (ctx is ResetCountingContext c)
                c.ResetCountAtSetup = c.ResetCount;
            return Task.CompletedTask;
        }

        public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
