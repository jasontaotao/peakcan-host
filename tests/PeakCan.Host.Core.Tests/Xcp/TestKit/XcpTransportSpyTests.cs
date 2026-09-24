using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.TestKit;

/// <summary>
/// XcpTransportSpy（S2-T6）：观测 master 发出的请求帧并透传 slave→master 帧，
/// FramesDropped 直通内层 transport。
/// </summary>
public class XcpTransportSpyTests
{
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    [Fact]
    public async Task Spy_records_sent_frames_in_order_and_forwards_responses()
    {
        await using var slave = new XcpVirtualSlave();
        var spy = new XcpTransportSpy(slave);
        using var master = new XcpMaster(spy, new XcpMasterOptions(MasterCanId));

        var response = await master.SendAsync(XcpCommandEncoder.Synch()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(XcpGoldenSamples.SynchPositiveResponse.ToArray(), response);
        Assert.Equal(1, spy.WriteCount);
        Assert.Equal(XcpGoldenSamples.SynchRequest.ToArray(), spy.Sent[0].Data.ToArray());
    }

    [Fact]
    public async Task Spy_forwards_frames_dropped_from_inner_transport()
    {
        await using var slave = new XcpVirtualSlave();
        var spy = new XcpTransportSpy(slave);

        slave.InjectDroppedDto(3);

        Assert.Equal(3, spy.FramesDropped);
    }

    [Fact]
    public async Task Spy_forwards_slave_frames_to_subscribers()
    {
        await using var slave = new XcpVirtualSlave();
        var spy = new XcpTransportSpy(slave);
        CanFrame? received = null;
        spy.FrameReceived += f => received = f;

        slave.InjectDto(0x02, 0xAA);

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 0x02, 0xAA }, received!.Value.Data.ToArray());
    }
}
