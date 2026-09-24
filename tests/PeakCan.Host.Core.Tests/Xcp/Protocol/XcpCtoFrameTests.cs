using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// XcpCtoFrame 基础行为：8B 硬约束 + PID 首字节三流分类（spec §3 Receive）。
/// </summary>
public class XcpCtoFrameTests
{
    [Fact]
    public void Empty_payload_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new XcpCtoFrame(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Payload_over_8_bytes_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new XcpCtoFrame(new byte[9]));
    }

    [Theory]
    [InlineData(0xFF, true, false, false, false, false)]
    [InlineData(0xFE, false, true, false, false, false)]
    [InlineData(0x00, false, false, true, false, false)]
    [InlineData(0xFB, false, false, true, false, false)]
    [InlineData(0xFC, false, false, false, false, true)]
    [InlineData(0xFD, false, false, false, true, false)]
    public void Pid_classifies_the_receive_streams(
        byte pid, bool positive, bool error, bool daq, bool eventPacket, bool serviceRequestPacket)
    {
        var frame = new XcpCtoFrame(new byte[] { pid, 0x01 });

        Assert.Equal(pid, frame.Pid);
        Assert.Equal(positive, frame.IsPositiveResponse);
        Assert.Equal(error, frame.IsError);
        Assert.Equal(daq, frame.IsDaqDto);
        Assert.Equal(eventPacket, frame.IsEventPacket);
        Assert.Equal(serviceRequestPacket, frame.IsServiceRequestPacket);
    }
}
