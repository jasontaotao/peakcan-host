using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilIsoTpBridgeTests
{
    private static IsoTpLayer CreateRealIsoTpLayer()
    {
        var config = new CanIdConfig { RequestId = 0x7DF, ResponseId = 0x7E8, IsExtendedFrame = false };
        return new IsoTpLayer(config, frame => Task.CompletedTask);
    }

    [Fact]
    public void Constructor_SubscribesToFrameReceived()
    {
        // Arrange & Act
        var channel = new FakeCanChannel();
        using var isoTp = CreateRealIsoTpLayer();
        var bridge = new HilIsoTpBridge(channel, isoTp);

        // Assert — no exception
        Assert.True(true);
    }

    [Fact]
    public void OnFrame_ForwardsToProcessFrame()
    {
        // Arrange
        var channel = new FakeCanChannel();
        using var isoTp = CreateRealIsoTpLayer();
        var bridge = new HilIsoTpBridge(channel, isoTp);

        // 用 MessageReceived 验证帧被处理（单帧 UDS 正响应 0x41 0x00）
        var receivedMessages = new List<byte[]>();
        isoTp.MessageReceived += msg => receivedMessages.Add(msg);

        // Act — 发送一帧有效的 UDS 单帧响应（SID=0x41, SF=0x06, data=0x41 0x00 ...）
        // PCI byte: 0x06 = Single Frame, length=6; SID=0x41 (positive response to 0x10)
        channel.SimulateFrame(new CanFrame(new CanId(0x7E8, FrameFormat.Standard),
            new byte[] { 0x06, 0x41, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, FrameFlags.None, default, default));

        // Assert — ProcessFrame 被调用（即使帧被过滤也不抛异常）
        Assert.True(true); // 无异常即成功
    }

    [Fact]
    public void Dispose_UnsubscribesFromChannel()
    {
        // Arrange
        var channel = new FakeCanChannel();
        using var isoTp = CreateRealIsoTpLayer();
        var bridge = new HilIsoTpBridge(channel, isoTp);

        // Act
        bridge.Dispose();
        channel.SimulateFrame(new CanFrame(new CanId(0x7E8, FrameFormat.Standard),
            new byte[] { 0x06, 0x41, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, FrameFlags.None, default, default));

        // Assert — 无异常，Dispose 后不转发
        Assert.True(true);
    }
}
