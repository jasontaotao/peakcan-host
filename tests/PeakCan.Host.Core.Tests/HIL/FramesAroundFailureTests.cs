using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using NSubstitute;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL;

/// <summary>
/// Fake assertion context that implements IHasRecentFrames for testing FramesAroundFailure.
/// </summary>
internal sealed class FakeAssertionContextWithRecent : IAssertionContext, IHasRecentFrames
{
    private readonly List<Action<DecodedFrame>> _subscribers = new();
    private readonly List<CanFrame> _recentFrames = new();
    private readonly Dictionary<string, double> _signalValues = new();

    public double CurrentTimestamp { get; set; }
    public System.Collections.Generic.IReadOnlyList<PeakCan.Host.Core.HIL.Contracts.DecodedFrame> GetRecentDecodedFrames() => Array.Empty<PeakCan.Host.Core.HIL.Contracts.DecodedFrame>();

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
    {
        _subscribers.Add(onFrame);
        return new FakeSubscription(() => _subscribers.Remove(onFrame));
    }

    public double? GetSignalValue(string signalName, int maxAgeMs = 5000) =>
        _signalValues.TryGetValue(signalName, out var v) ? v : null;

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct = default) =>
        ValueTask.FromResult(Result<Unit>.Ok(default));

    public IReadOnlyList<CanFrame> GetRecentFrames() => _recentFrames.AsReadOnly();

    public void PushFrame(CanFrame frame)
    {
        _recentFrames.Add(frame);
        var decoded = new DecodedFrame(frame, new Dictionary<string, double>());
        foreach (var sub in _subscribers.ToList())
            sub(decoded);
    }

    private sealed class FakeSubscription : IDisposable
    {
        private Action? _dispose;
        public FakeSubscription(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public class FramesAroundFailureTests
{
    [Fact]
    public async Task StepFailure_CapturesRecentFrames()
    {
        // Arrange
        var ctx = new FakeAssertionContextWithRecent();
        ctx.PushFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default));
        ctx.PushFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, default, default));
        ctx.PushFrame(new CanFrame(new CanId(0x300, FrameFormat.Standard),
            new byte[] { 0x03 }, FrameFlags.None, default, default));

        var engine = new TestSuiteEngine(
            Substitute.For<IFixtureResolver>(),
            new IStepExecutor[] { new FailingStepExecutor() });

        var suite = new TestSuite("TestSuite",
            new[]
            {
                new TestCase("case_1", "Failing Case", "", null,
                    new[] { TestCaseStep.Create(new AssertSignalStep("Sig", 100.0, 5.0)) },
                    null, Array.Empty<string>())
            },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        // Act
        var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

        // Assert
        var caseResult = result.CaseResults[0];
        Assert.False(caseResult.Passed);
        var failedStep = caseResult.StepResults.First(r => r.Status == StepStatus.Failed);
        Assert.NotNull(failedStep.FramesAroundFailure);
        Assert.Equal(3, failedStep.FramesAroundFailure!.Count);
    }

    [Fact]
    public async Task StepPassed_NoFramesCaptured()
    {
        // Arrange
        var ctx = new FakeAssertionContextWithRecent();
        var engine = new TestSuiteEngine(
            Substitute.For<IFixtureResolver>(),
            new IStepExecutor[] { new PassingStepExecutor() });

        var suite = new TestSuite("TestSuite",
            new[]
            {
                new TestCase("case_1", "Passing Case", "", null,
                    new[] { TestCaseStep.Create(new AssertSignalStep("Sig", 100.0, 5.0)) },
                    null, Array.Empty<string>())
            },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        // Act
        var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

        // Assert
        var caseResult = result.CaseResults[0];
        Assert.True(caseResult.Passed);
        var step = caseResult.StepResults[0];
        Assert.Null(step.FramesAroundFailure);
    }

    /// <summary>
    /// Step executor that always fails.
    /// </summary>
    private sealed class FailingStepExecutor : IStepExecutor
    {
        public TestCaseStepKind Kind => TestCaseStepKind.AssertSignal;
        public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct) =>
            Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "always fails", null, null, 0));
    }

    /// <summary>
    /// Step executor that always passes.
    /// </summary>
    private sealed class PassingStepExecutor : IStepExecutor
    {
        public TestCaseStepKind Kind => TestCaseStepKind.AssertSignal;
        public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct) =>
            Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                "always passes", null, null, 0));
    }
}
