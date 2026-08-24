using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 3 (phase 2 A-3): ChannelConnection 单项 VM 测试。
/// 验证单项 DisconnectCommand 断开底层 channel + 状态置"已断开"。
/// ChannelConnection 不持 shell 引用（C2 ruling）——单项断开只需 channel.DisconnectAsync。
/// </summary>
public sealed class ChannelConnectionTests
{
    [Fact]
    public async Task DisconnectCommand_DisconnectsUnderlyingChannel_AndSetsState()
    {
        // Arrange
        var channel = Substitute.For<ICanChannel>();
        channel.Id.Returns(new ChannelId(0x51));
        var conn = new ChannelConnection(channel, "bus-a", BaudRate.CanFd1Mbps);
        conn.State = "已连接";

        // Act
        await conn.DisconnectCommand.ExecuteAsync(null);

        // Assert: 底层 channel 断开一次 + 状态置"已断开"
        await channel.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        Assert.Equal("已断开", conn.State);
    }

    [Fact]
    public void Constructor_SetsChannelNameBaudRate_DefaultStateConnected()
    {
        var channel = Substitute.For<ICanChannel>();
        var conn = new ChannelConnection(channel, "bus-b", BaudRate.Can500kbps);

        Assert.Same(channel, conn.Channel);
        Assert.Equal("bus-b", conn.Name);
        Assert.Equal(BaudRate.Can500kbps, conn.BaudRate);
        Assert.Equal("已连接", conn.State);
    }
}
