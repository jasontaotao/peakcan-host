using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.TestKit;

/// <summary>
/// XcpVirtualSlave 模拟从机（S2-T6）：脚本化规则验证——
/// 黄金样本正/负响应回放、篡改声明值（T7 对账用）、silence/延迟/丢帧注入、
/// 错误码注入。FramesDropped 语义须与 IXcpTransport 契约一致：
/// 仅 DTO 流丢弃计数，响应帧（0xFF/0xFE）永不计入。
/// </summary>
public class XcpVirtualSlaveTests
{
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(2000);
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    /// <summary>黄金样本全量命令 → 正响应映射（spec §1 硬约束编码于样本内）。</summary>
    public static TheoryData<Func<XcpCtoFrame>, byte[]> GoldenCommands => new()
    {
        { () => XcpCommandEncoder.Connect(), XcpGoldenSamples.ConnectPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.Disconnect(), XcpGoldenSamples.DisconnectPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.GetStatus(), XcpGoldenSamples.GetStatusPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.Synch(), XcpGoldenSamples.SynchPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.GetCommModeInfo(), XcpGoldenSamples.GetCommModeInfoPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.SetMta(0, 0x1000), XcpGoldenSamples.SetMtaPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.Upload(4), XcpGoldenSamples.UploadPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.ShortUpload(2, 0x2000, 0), XcpGoldenSamples.ShortUploadPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.Download(stackalloc byte[] { 0xAB, 0xCD }), XcpGoldenSamples.DownloadPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.SetDaqPtr(0, 0), XcpGoldenSamples.SetDaqPtrPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.WriteDaq(0, 2, 0, 0x1000), XcpGoldenSamples.WriteDaqPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.ClearDaqList(0, 0), XcpGoldenSamples.ClearDaqListPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.StartStopDaqList(0, 0), XcpGoldenSamples.StartStopDaqListPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.StartStopSynch(), XcpGoldenSamples.StartStopSynchPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.GetDaqProcessorInfo(), XcpGoldenSamples.GetDaqProcessorInfoPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.GetDaqResolutionInfo(), XcpGoldenSamples.GetDaqResolutionInfoPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.GetDaqListInfo(0), XcpGoldenSamples.GetDaqListInfoPositiveResponse.ToArray() },
        { () => XcpCommandEncoder.GetDaqEventInfo(0), XcpGoldenSamples.GetDaqEventInfoPositiveResponse.ToArray() },
    };

    [Theory]
    [MemberData(nameof(GoldenCommands))]
    public async Task Golden_command_replays_its_positive_response(Func<XcpCtoFrame> command, byte[] expected)
    {
        await using var slave = new XcpVirtualSlave();

        var response = await SendAsync(slave, command());

        Assert.Equal(expected, response);
    }

    [Fact]
    public async Task Connect_handshake_replays_golden_request_response_pair()
    {
        var spy = new XcpTransportSpy(new XcpVirtualSlave());
        using var master = new XcpMaster(spy, new XcpMasterOptions(MasterCanId));

        var response = await master.SendAsync(XcpCommandEncoder.Connect()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(XcpGoldenSamples.ConnectPositiveResponse.ToArray(), response);
        Assert.Equal(1, spy.WriteCount);
        Assert.Equal(XcpGoldenSamples.ConnectRequest.ToArray(), spy.Sent[0].Data.ToArray());
    }

    [Fact]
    public async Task Unknown_command_pid_gets_cmd_unknown_negative_response()
    {
        await using var slave = new XcpVirtualSlave();

        var ex = await Assert.ThrowsAsync<XcpErrorResponseException>(
            () => SendAsync(slave, new XcpCtoFrame(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })));

        Assert.Equal(XcpError.CmdUnknown, ex.Response.Code);
    }

    // 篡改声明值入口（T7 对账用例）：脚本化负响应覆盖黄金样本默认值。
    [Fact]
    public async Task Scripted_negative_response_overrides_golden_default()
    {
        await using var slave = new XcpVirtualSlave();
        slave.OverrideNegative(XcpPid.Connect, XcpError.OutOfRange);

        var ex = await Assert.ThrowsAsync<XcpErrorResponseException>(
            () => SendAsync(slave, XcpCommandEncoder.Connect()));

        Assert.Equal(XcpError.OutOfRange, ex.Response.Code);
    }

    // 篡改正响应载体字节（T7 用篡改后的声明字段喂对账层）。
    [Fact]
    public async Task Scripted_positive_response_overrides_golden_default()
    {
        await using var slave = new XcpVirtualSlave();
        var tampered = XcpGoldenSamples.ConnectPositiveResponse.ToArray();
        tampered[3] = 0x07; // resources 篡改
        slave.OverrideResponse(XcpPid.Connect, tampered);

        var response = await SendAsync(slave, XcpCommandEncoder.Connect());

        Assert.Equal(tampered, response);
    }

    // silence → master T1 超时，归因 SlaveNoResponse；响应帧被"丢弃"不计入 FramesDropped。
    [Fact]
    public async Task Scripted_silence_times_out_with_slave_no_response_and_zero_drop_delta()
    {
        await using var slave = new XcpVirtualSlave();
        slave.Silence(XcpPid.Synch);
        var time = new FakeTimeProvider();
        using var master = new XcpMaster(slave, new XcpMasterOptions(MasterCanId, maxRetries: 0), time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        time.Advance(T1);

        var ex = await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(XcpTimeoutCause.SlaveNoResponse, ex.Attribution);
        Assert.Equal(0, ex.FramesDroppedDelta);
        Assert.Equal(0, slave.FramesDropped);
    }

    // 延迟注入：响应迟到但仍到达（真实时钟；20ms 远小于默认 T1=2000ms）。
    [Fact]
    public async Task Scripted_delay_defers_response_but_master_still_receives_it()
    {
        await using var slave = new XcpVirtualSlave();
        slave.DelayResponse(XcpPid.Synch, TimeSpan.FromMilliseconds(20));
        using var master = new XcpMaster(slave, new XcpMasterOptions(MasterCanId));

        var response = await SendAsync(slave, XcpCommandEncoder.Synch());

        Assert.Equal(XcpGoldenSamples.SynchPositiveResponse.ToArray(), response);
    }

    // DTO 注入按 PID 首字节到达订阅者（三流共用 CAN_ID_SLAVE 的分流基线）。
    [Fact]
    public async Task Injected_dto_reaches_subscriber_with_odt_pid()
    {
        await using var slave = new XcpVirtualSlave();
        CanFrame? received = null;
        slave.FrameReceived += f => received = f;

        slave.InjectDto(0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);

        Assert.NotNull(received);
        Assert.Equal(
            new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 },
            received!.Value.Data.ToArray());
    }

    // 错位帧/垃圾帧注入：任意 slave→master 字节流原样出帧。
    [Fact]
    public async Task Injected_arbitrary_frame_reaches_subscriber_unchanged()
    {
        await using var slave = new XcpVirtualSlave();
        CanFrame? received = null;
        slave.FrameReceived += f => received = f;

        slave.InjectFrame(XcpPid.EventPacket, 0x05, 0x00);

        Assert.NotNull(received);
        Assert.Equal(new byte[] { XcpPid.EventPacket, 0x05, 0x00 }, received!.Value.Data.ToArray());
    }

    // FramesDropped 语义（IXcpTransport 契约）：只有 DTO 流丢弃计数；
    // 响应帧被 silence 掉不计数；错误码负响应也不计数。
    [Fact]
    public async Task Frames_dropped_counts_only_dto_drops_not_response_frames()
    {
        await using var slave = new XcpVirtualSlave();
        slave.Silence(XcpPid.Synch);
        var time = new FakeTimeProvider();
        using var master = new XcpMaster(slave, new XcpMasterOptions(MasterCanId, maxRetries: 0), time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        time.Advance(T1);
        await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, slave.FramesDropped); // 响应帧丢弃不计入

        slave.InjectDroppedDto(2);
        Assert.Equal(2, slave.FramesDropped);
    }

    [Fact]
    public async Task Dto_injection_rejects_payload_over_seven_bytes()
    {
        await using var slave = new XcpVirtualSlave();

        Assert.Throws<ArgumentException>(() => slave.InjectDto(0x00, 1, 2, 3, 4, 5, 6, 7, 8));
    }

    [Fact]
    public async Task Frame_injection_rejects_empty_data()
    {
        await using var slave = new XcpVirtualSlave();

        Assert.Throws<ArgumentException>(() => slave.InjectFrame());
    }

    private static async Task<byte[]> SendAsync(IXcpTransport transport, XcpCtoFrame command)
    {
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId));
        return await master.SendAsync(command).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
