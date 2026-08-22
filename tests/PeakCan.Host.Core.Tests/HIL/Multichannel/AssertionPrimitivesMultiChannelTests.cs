using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Assertions;
using PeakCan.HIL.Core.HIL.Contracts;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

/// <summary>
/// 多通道 WaitForFrameAsync 5-param 重载 + HilRunRequest.HardwareChannels 验证。
/// Fake IAssertionContext 显式实现 channel 重载以捕获 channelName（证明路由到通道版而非单通道版）。
/// </summary>
public sealed class AssertionPrimitivesMultiChannelTests
{
    [Fact]
    public async Task WaitForFrameAsync_WithChannelName_CallsChannelOverloads()
    {
        // Arrange
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        var expectedId = new CanId(0x123, FrameFormat.Standard);

        // Act
        var result = await primitives.WaitForFrameAsync(
            expectedId, dataMask: null, timeoutMs: 5000, channelName: "ch1", CancellationToken.None);

        // Assert: channelName was routed to channel overloads
        Assert.Equal("ch1", ctx.CapturedChannelName);
        Assert.True(result.Passed, $"Expected pass but got: {result.Message}");
    }

    [Fact]
    public async Task WaitForFrameAsync_WithNullChannelName_ForwardsToSingleChannel()
    {
        // Arrange
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        var expectedId = new CanId(0x456, FrameFormat.Standard);

        // Act: channelName=null should NOT trigger channel overload (DIM forwards to single-channel)
        var result = await primitives.WaitForFrameAsync(
            expectedId, dataMask: null, timeoutMs: 5000, channelName: null, CancellationToken.None);

        // Assert: CapturedChannelName stays null (channel overload never called);
        // the fake's single-channel SubscribeDecodedFrames is no-op so we should get timeout
        Assert.Null(ctx.CapturedChannelName);
        Assert.False(result.Passed);
        Assert.Contains("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitForFrameAsync_Legacy4Param_StillWorks()
    {
        // Arrange: legacy 4-param overload, no channelName
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        var expectedId = new CanId(0x789, FrameFormat.Standard);

        // Act: old 4-param signature
        var result = await primitives.WaitForFrameAsync(
            expectedId, dataMask: null, timeoutMs: 5000, CancellationToken.None);

        // Assert: CapturedChannelName stays null (channel overload never called);
        // fake's single-channel SubscribeDecodedFrames is no-op → timeout
        Assert.Null(ctx.CapturedChannelName);
        Assert.False(result.Passed);
        Assert.Contains("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HilRunRequest_HardwareChannels_DefaultIsNull()
    {
        // Arrange/Act
        var req = new HilRunRequest(
            DbcPath: "test.dbc",
            SuitePath: "suite.json");

        // Assert
        Assert.Null(req.HardwareChannels);
    }

    [Fact]
    public void HilRunRequest_HardwareChannels_CanBeSetAndRead()
    {
        // Arrange
        var channels = new List<ChannelConfig>
        {
            new ChannelConfig("ch1", "CANale1", null, false, null, null, null),
            new ChannelConfig("ch2", "CANale2", null, false, null, null, null),
        };

        // Act
        var req = new HilRunRequest(
            DbcPath: "test.dbc",
            SuitePath: "suite.json",
            HardwareChannels: channels);

        // Assert
        Assert.NotNull(req.HardwareChannels);
        Assert.Equal(2, req.HardwareChannels.Count);
        Assert.Equal("ch1", req.HardwareChannels[0].Name);
        Assert.Equal("ch2", req.HardwareChannels[1].Name);
    }

    // ── Fake IAssertionContext ──

    private sealed class FakeAssertionContext : IAssertionContext
    {
        /// <summary>Captured channelName from the last channel-overload call.</summary>
        public string? CapturedChannelName { get; private set; }

        // Single-channel methods (minimal implementations)
        public double CurrentTimestamp => 0;
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => new(Result<Unit>.Ok(default));
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new StubDisposable();

        // Multi-channel overloads — explicit interface implementation (override DIM, capture channelName)
        IReadOnlyList<DecodedFrame> IAssertionContext.GetRecentDecodedFrames(string? channelName)
        {
            CapturedChannelName = channelName;
            return Array.Empty<DecodedFrame>();
        }

        IDisposable IAssertionContext.SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
        {
            CapturedChannelName = channelName;
            // Synchronously invoke with a matching frame so WaitForFrameAsync passes immediately
            var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01, 0x02 },
                FrameFlags.None, default, default);
            onFrame(new DecodedFrame(frame, new Dictionary<string, double>()));
            return new StubDisposable();
        }

        ValueTask<Result<Unit>> IAssertionContext.SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
        {
            CapturedChannelName = channelName;
            return new(Result<Unit>.Ok(default));
        }
    }

    private sealed class StubDisposable : IDisposable
    {
        public void Dispose() { }
    }
}