using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL;

/// <summary>
/// The engine must invoke the per-case reset capability exactly once per case,
/// before case setup, so cumulative subsystem state (SecOC verdict stats) cannot
/// leak across cases (spec Rev7). Mirrors the existing per-case variable clear.
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

    [Fact]
    public async Task ExecuteAsync_CallsResetPerCase_OncePerCase()
    {
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), Array.Empty<IStepExecutor>());
        var ctx = new ResetCountingContext();

        var result = await engine.ExecuteAsync(TwoCaseSuite(), ctx, new TestSuiteConfig());

        Assert.Equal(2, result.CaseResults.Count);
        Assert.Equal(2, ctx.ResetCount);
    }

    private sealed class ResetCountingContext : IAssertionContext, IPerCaseReset
    {
        public int ResetCount;
        public void ResetPerCase() => ResetCount++;

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }
}
