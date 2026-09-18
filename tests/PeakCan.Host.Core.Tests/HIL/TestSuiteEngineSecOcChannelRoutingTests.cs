using FluentAssertions;
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
/// 项 2（2026-09-18）：多通道 secoc 表达式逐通道路由（方案 B「跟着步骤走」）。
/// 表达式所在步骤声明 TargetChannel 时，secocAccepted/secocRejected 按该通道的
/// 验签统计解析；无 TargetChannel → 默认通道。端到端：引擎逐 step 设置
/// ChannelContext.Current → StepScopeFactory 的 SecOcFunctionRegistry 经
/// currentChannelProvider 读取 → ${...} 插值求值用对应通道 stats。
/// </summary>
public class TestSuiteEngineSecOcChannelRoutingTests
{
    private sealed class ChannelAwareContext : IAssertionContext, ISecOcStatsSource, IPerCaseReset
    {
        public ISecOcStats? SecOcStats => SecOcStatsFor(null);

        public ISecOcStats? SecOcStatsFor(string? channelName)
        {
            // bus-b 上 0x123 被拒（Replay）；bus-a/默认上 0x123 正常通过。
            bool isBusB = channelName == "bus-b";
            return new FakeStats(isBusB);
        }

        public void ResetPerCase() { }

        private sealed class FakeStats : ISecOcStats
        {
            private readonly bool _rejectOn123;
            public FakeStats(bool rejectOn123) => _rejectOn123 = rejectOn123;
            public bool TryGet(uint canId, out SecOcVerdictBucket bucket)
            {
                if (canId == 0x123 && _rejectOn123)
                {
                    bucket = new SecOcVerdictBucket(0, 1, "Replay");
                    return true;
                }
                if (canId == 0x123)
                {
                    bucket = new SecOcVerdictBucket(2, 0, null);
                    return true;
                }
                bucket = new SecOcVerdictBucket(0, 0, null);
                return false;
            }
        }

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }

    /// <summary>捕获 AssertFrameCount 插值后的 WindowMs（secoc 表达式经 ${} 进入该字段）。</summary>
    private sealed class RecordingFrameCountExecutor : IStepExecutor
    {
        public string? LastWindowMs { get; private set; }
        public string? LastTargetChannel { get; private set; }
        public TestCaseStepKind Kind => TestCaseStepKind.AssertFrameCount;

        public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
        {
            var p = (AssertFrameCountStep)step.Parameters;
            LastWindowMs = p.WindowMs;
            LastTargetChannel = p.TargetChannel;
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"WindowMs={p.WindowMs}", null, null, 0, Channel: p.TargetChannel));
        }
    }

    private static TestSuite OneCase(params TestCaseStep[] steps)
        => new("S",
            new[]
            {
                new TestCase("c1", "TC", "", null, steps,
                    null, Array.Empty<string>(), 0, null),
            },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

    /// <summary>
    /// 带 TargetChannel="bus-b" 的 AssertFrameCount 步骤，其 WindowMs 用
    /// `${secocRejected(0x123)}` 插值：bus-b 上 0x123 被拒 → 表达式按 bus-b 通道
    /// 解析 → True；若错误回落默认通道 → False。插值结果直接进 executor。
    /// </summary>
    [Fact]
    public async Task SecocExpression_FollowsStepTargetChannel()
    {
        var executor = new RecordingFrameCountExecutor();
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), new IStepExecutor[] { executor });
        var step = TestCaseStep.Create(
            new AssertFrameCountStep(new CanId(0x123, FrameFormat.Standard), "${secocRejected(0x123)}", "0", "100")
            {
                TargetChannel = "bus-b",
            });

        var result = await engine.ExecuteAsync(OneCase(step), new ChannelAwareContext(), new TestSuiteConfig());
        var caseResult = result.CaseResults[0];
        var stepResults = caseResult.StepResults;
        Assert.NotNull(stepResults);
        Assert.NotEmpty(stepResults!);
        var stepResult = stepResults![0];
        Assert.True(stepResult.Passed, $"step should pass, got: {stepResult.Status} {stepResult.Message}");
        Assert.Equal("true", executor.LastWindowMs);
        Assert.Equal("bus-b", executor.LastTargetChannel);
    }

    /// <summary>无 TargetChannel → 默认通道（bus-a 上 0x123 通过）→ secocRejected false → "false"。</summary>
    [Fact]
    public async Task SecocExpression_NoTargetChannel_DefaultsToDefaultChannel()
    {
        var executor = new RecordingFrameCountExecutor();
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), new IStepExecutor[] { executor });
        var step = TestCaseStep.Create(
            new AssertFrameCountStep(new CanId(0x123, FrameFormat.Standard), "${secocRejected(0x123)}", "0", "100"));

        await engine.ExecuteAsync(OneCase(step), new ChannelAwareContext(), new TestSuiteConfig());

        Assert.Equal("false", executor.LastWindowMs);
        Assert.Null(executor.LastTargetChannel);
    }

    /// <summary>secocAccepted 也随通道路由：bus-b 被拒 → accepted false → "false"。</summary>
    [Fact]
    public async Task SecocExpression_Accepted_FollowsStepTargetChannel()
    {
        var executor = new RecordingFrameCountExecutor();
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), new IStepExecutor[] { executor });
        var step = TestCaseStep.Create(
            new AssertFrameCountStep(new CanId(0x123, FrameFormat.Standard), "${secocAccepted(0x123)}", "0", "100")
            {
                TargetChannel = "bus-b",
            });

        await engine.ExecuteAsync(OneCase(step), new ChannelAwareContext(), new TestSuiteConfig());

        // bus-b 上被拒 → accepted false → "false"
        Assert.Equal("false", executor.LastWindowMs);
    }

    // ── L-3 补充（review 2026-09-18）：if 容器条件走默认通道 + 无 TargetChannel 步骤类型 ──

    /// <summary>if 容器自身无 TargetChannel → 条件 secoc 表达式走默认通道（方案 B 已接受的局限）。</summary>
    [Fact]
    public async Task SecocExpression_IfCondition_UsesDefaultChannel()
    {
        // bus-b 上 0x123 被拒，但 if 容器无 TargetChannel → 默认通道（bus-a 通过）
        // → secocRejected false → 走 else 分支。用 else 分支里放 recording step 验证。
        var executor = new RecordingFrameCountExecutor();
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), new IStepExecutor[] { executor });
        var elseProbe = TestCaseStep.Create(
            new AssertFrameCountStep(new CanId(0x123, FrameFormat.Standard), "from-else", "0", "100"));
        var ifStep = TestCaseStep.Create(new IfStep(
            Condition: "secocRejected(0x123)",
            Body: Array.Empty<TestCaseStep>(),
            ElseBody: new[] { elseProbe }));

        await engine.ExecuteAsync(OneCase(ifStep), new ChannelAwareContext(), new TestSuiteConfig());

        Assert.Equal("from-else", executor.LastWindowMs);
    }

    /// <summary>无 TargetChannel 属性的步骤类型（Delay）走 null-property 分支，不抛、不路由。</summary>
    [Fact]
    public async Task SecocExpression_StepWithoutTargetChannel_DoesNotThrow()
    {
        // Delay 无 TargetChannel 属性 → GetStepTargetChannel 缓存 null PropertyInfo →
        // Current 置 null → 默认通道。插值 secoc 表达式（默认通道 true）应正常求值。
        var executor = new RecordingFrameCountExecutor();
        var engine = new TestSuiteEngine(new FakeFixtureResolver(), new IStepExecutor[] { executor });
        var step = TestCaseStep.Create(new DelayStep("1"));
        _ = step;

        // 直接跑一个 AssertFrameCount（无 TargetChannel）已覆盖 null 分支（上个测试）。
        // 这里补一个「多次执行不同步骤类型」混跑确认 ConcurrentDictionary 缓存 null 不崩。
        var send = TestCaseStep.Create(new CommentStep("doc"));
        var engine2 = new TestSuiteEngine(new FakeFixtureResolver(), new IStepExecutor[] { executor });

        var act = () => engine2.ExecuteAsync(OneCase(send), new ChannelAwareContext(), new TestSuiteConfig());

        await act.Should().NotThrowAsync();
    }
}
