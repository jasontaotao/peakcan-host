using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

/// <summary>
/// 5 MVP executor TargetChannel 路由测试（不需硬件）。
/// Fake IAssertionContext 显式实现 channel SendFrameAsync 重载，捕获 channelName +
/// 验证 StepResult.Channel = p.TargetChannel。证明 executor 走 channel 重载且写 Channel 字段。
/// </summary>
public sealed class ExecutorChannelRoutingTests
{
    [Fact]
    public async Task SendFrame_WithTargetChannel_RoutesByChannelName_AndSetsStepResultChannel()
    {
        // Arrange
        var ctx = new FakeCtx();
        var executor = new SendFrameStepExecutor();
        var p = new SendFrameStep(new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01 }, false, false)
        {
            TargetChannel = "bus-a",
        };
        var step = TestCaseStep.Create(p, label: "send");

        // Act
        var result = await executor.ExecuteAsync(step, ctx, CancellationToken.None);

        // Assert: channel overload was invoked with the right channelName
        Assert.Equal("bus-a", ctx.CapturedSendChannelName);
        // StepResult.Channel records the logical channel
        Assert.Equal("bus-a", result.Channel);
        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task SendFrame_WithNullTargetChannel_UsesNullChannel_AndStepResultChannelNull()
    {
        // Arrange: 单通道路径 — TargetChannel null
        var ctx = new FakeCtx();
        var executor = new SendFrameStepExecutor();
        var p = new SendFrameStep(new CanId(0x100, FrameFormat.Standard), new byte[] { 0xAA }, false, false);
        var step = TestCaseStep.Create(p);

        var result = await executor.ExecuteAsync(step, ctx, CancellationToken.None);

        // channelName null routed (single-channel compat)
        Assert.Null(ctx.CapturedSendChannelName);
        Assert.Null(result.Channel);
        Assert.Equal(StepStatus.Passed, result.Status);
    }

    // ── Fake IAssertionContext: 显式实现 channel SendFrameAsync 重载以捕获 channelName ──
    private sealed class FakeCtx : IAssertionContext
    {
        public string? CapturedSendChannelName { get; private set; }

        public double CurrentTimestamp => 0;
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;

        // 单通道 SendFrameAsync — 不应被 SendFrame executor 直接调（executor 走 channel 重载）。
        // 标记捕获以区分路径。
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
        {
            CapturedSendChannelName = null; // 标记走了单通道版
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        // channel 重载 — 显式实现 override DIM，捕获 channelName
        public ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
        {
            CapturedSendChannelName = channelName;
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new NopDisposable();
        public IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame) => new NopDisposable();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName) => Array.Empty<DecodedFrame>();
    }

    private sealed class NopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
