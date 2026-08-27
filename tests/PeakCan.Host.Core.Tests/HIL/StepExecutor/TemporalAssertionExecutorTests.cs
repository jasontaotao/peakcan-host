using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

/// <summary>
/// Task C (spec 2026-08-27 §3.3): AssertSignalWithin / AssertStable executor 测试。
/// 窗口收集依赖 IAssertionContext.SubscribeDecodedFrames + GetSignalValue 缓存快照
/// （样本口径与 WaitForSignalAsync 一致，见 AssertionPrimitives.cs:23-28）。
/// 用 ManualAssertionContext 手动喂帧/设值精确控制窗口样本。
/// </summary>
public class TemporalAssertionExecutorTests
{
    private static readonly CanFrame DummyFrame = new(
        new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01 }, FrameFlags.None, default, default);

    /// <summary>可控 assertion context：EmitFrame 触发订阅回调，回调内 GetSignalValue 返回当前 SignalValue 快照。</summary>
    private sealed class ManualAssertionContext : IAssertionContext
    {
        public double? SignalValue { get; set; } = 0;
        public double Timestamp { get; set; }
        public string? SubscribedChannel { get; private set; }

        private Action<DecodedFrame>? _handler;

        public IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
        {
            SubscribedChannel = channelName;
            _handler = onFrame;
            return new NullDisposable();
        }

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
            => SubscribeDecodedFrames(null, onFrame);

        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => SignalValue;
        public double CurrentTimestamp => Timestamp;

        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
            => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
            => throw new NotSupportedException();

        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName) => Array.Empty<DecodedFrame>();

        /// <summary>模拟一帧解码到达：触发订阅回调（回调内取当前 SignalValue 快照）。</summary>
        public void EmitFrame() => _handler?.Invoke(new DecodedFrame(DummyFrame, new Dictionary<string, double>()));
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }

    // ---- AssertSignalWithin ----

    [Fact]
    public async Task AssertSignalWithin_Any_OneHitWithinWindow_Passes()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200")), ctx, default);

        await Task.Delay(20);
        ctx.SignalValue = 97; ctx.EmitFrame();
        ctx.SignalValue = 103; ctx.EmitFrame();   // 103 ∈ [95,105] → ≥1 命中

        var result = await task;
        result.Status.Should().Be(StepStatus.Passed);
    }

    [Fact]
    public async Task AssertSignalWithin_Any_NoHit_Fails()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200")), ctx, default);

        await Task.Delay(20);
        ctx.SignalValue = 80; ctx.EmitFrame();
        ctx.SignalValue = 120; ctx.EmitFrame();   // 均超出 [95,105]

        var result = await task;
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task AssertSignalWithin_Any_ZeroSamples_Fails()
    {
        // 窗口内无帧（无样本）→ Any 零命中自然 Failed
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200")), ctx, default);

        var result = await task;   // 不 EmitFrame
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task AssertSignalWithin_All_AllSamplesHit_Passes()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200",
                MatchMode.All)), ctx, default);

        await Task.Delay(20);
        ctx.SignalValue = 100; ctx.EmitFrame();
        ctx.SignalValue = 103; ctx.EmitFrame();   // 全部 ∈ [95,105]

        var result = await task;
        result.Status.Should().Be(StepStatus.Passed);
    }

    [Fact]
    public async Task AssertSignalWithin_All_OneMiss_Fails()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200",
                MatchMode.All)), ctx, default);

        await Task.Delay(20);
        ctx.SignalValue = 100; ctx.EmitFrame();
        ctx.SignalValue = 80; ctx.EmitFrame();   // 80 越界 → 非全部命中

        var result = await task;
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task AssertSignalWithin_All_ZeroSamples_Fails()
    {
        // 防空窗口（spec §3.3）：All 且零有效样本 → Failed，不得 vacuous pass
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200",
                MatchMode.All)), ctx, default);

        var result = await task;   // 不 EmitFrame
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task AssertSignalWithin_NullSamples_ExcludedFromSampleCount()
    {
        // 报文整体缺失时快照为 null → 不计入样本（spec §3.3）；Any 下仅剩的命中样本仍应 Pass
        var ctx = new ManualAssertionContext { SignalValue = null };
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200")), ctx, default);

        await Task.Delay(20);
        ctx.EmitFrame();                          // null 快照 → 不计样本
        ctx.SignalValue = 100; ctx.EmitFrame();   // 命中样本

        var result = await task;
        result.Status.Should().Be(StepStatus.Passed);
    }

    [Fact]
    public async Task AssertSignalWithin_InvalidWindow_FailsFast()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "0")), ctx, default);

        result.Status.Should().Be(StepStatus.Failed);
        result.Message.Should().Contain("WindowMs");
    }

    [Fact]
    public async Task AssertSignalWithin_RoutesToTargetChannel()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertSignalWithinStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertSignalWithinStep("BMS.EngineRPM", "100", "5", "200")
            {
                TargetChannel = "bus-a",
            }), ctx, default);

        await Task.Delay(20);
        ctx.SignalValue = 100; ctx.EmitFrame();

        var result = await task;
        result.Status.Should().Be(StepStatus.Passed);
        ctx.SubscribedChannel.Should().Be("bus-a");   // 订阅按 channelName 路由
        result.Channel.Should().Be("bus-a");          // StepResult 带路由结果
    }

    // ---- AssertStable ----

    [Fact]
    public async Task AssertStable_WithinDelta_Passes()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertStableStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertStableStep("BMS.EngineRPM", "200", "5", "3")), ctx, default);

        await Task.Delay(20);
        foreach (var v in new[] { 100.0, 101.0, 100.0, 99.0 })
        {
            ctx.SignalValue = v; ctx.EmitFrame();
        }   // max-min = 2 ≤ 5

        var result = await task;
        result.Status.Should().Be(StepStatus.Passed);
    }

    [Fact]
    public async Task AssertStable_ExceedsDelta_Fails()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertStableStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertStableStep("BMS.EngineRPM", "200", "5", "3")), ctx, default);

        await Task.Delay(20);
        foreach (var v in new[] { 100.0, 120.0, 100.0 })
        {
            ctx.SignalValue = v; ctx.EmitFrame();
        }   // max-min = 20 > 5

        var result = await task;
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task AssertStable_InsufficientSamples_Fails()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertStableStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertStableStep("BMS.EngineRPM", "200", "5", "5")), ctx, default);

        await Task.Delay(20);
        foreach (var v in new[] { 100.0, 101.0, 100.0 })
        {
            ctx.SignalValue = v; ctx.EmitFrame();
        }   // 3 样本 < MinSamples=5

        var result = await task;
        result.Status.Should().Be(StepStatus.Failed);
        result.Message.Should().Contain("need >= 5");
    }

    [Fact]
    public async Task AssertStable_ZeroSamples_Fails()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertStableStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertStableStep("BMS.EngineRPM", "200", "5", "3")), ctx, default);

        var result = await task;   // 不 EmitFrame
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task AssertStable_InvalidParams_FailsFast()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertStableStepExecutor();
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new AssertStableStep("BMS.EngineRPM", "0", "5", "3")), ctx, default);

        result.Status.Should().Be(StepStatus.Failed);
        result.Message.Should().Contain("WindowMs");
    }

    [Fact]
    public async Task AssertStable_RoutesToTargetChannel()
    {
        var ctx = new ManualAssertionContext();
        var executor = new AssertStableStepExecutor();
        var task = executor.ExecuteAsync(
            TestCaseStep.Create(new AssertStableStep("BMS.EngineRPM", "200", "5", "3")
            {
                TargetChannel = "bus-b",
            }), ctx, default);

        await Task.Delay(20);
        foreach (var v in new[] { 100.0, 101.0, 100.0 })   // 3 样本 ≥ MinSamples=3
        {
            ctx.SignalValue = v; ctx.EmitFrame();
        }

        var result = await task;
        result.Status.Should().Be(StepStatus.Passed);
        ctx.SubscribedChannel.Should().Be("bus-b");
        result.Channel.Should().Be("bus-b");
    }
}
