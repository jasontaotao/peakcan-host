using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// SET_DAQ_PTR / WRITE_DAQ / CLEAR_DAQ_LIST / START_STOP_DAQ_LIST / START_STOP_SYNCH /
/// GET_DAQ_PROCESSOR_INFO / GET_DAQ_RESOLUTION_INFO / GET_DAQ_LIST_INFO / GET_DAQ_EVENT_INFO
/// 字节级黄金样本（spec §5 验收 1）。
/// START_STOP 仅 mode 0/1 合法（mode 2 编码器层直接拒绝——spec §1 写死不用 select 路径）。
/// </summary>
public class DaqCommandsTests
{
    // ---- SET_DAQ_PTR ----

    [Fact]
    public void SetDaqPtr_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.SetDaqPtr(addressExtension: 0x00, address: 0x00000000);

        Assert.True(
            XcpGoldenSamples.SetDaqPtrRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.SetDaqPtrRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void SetDaqPtr_positive_response_is_single_pid()
    {
        XcpResponseDecoder.SetDaqPtr(XcpGoldenSamples.SetDaqPtrPositiveResponse.Span);
    }

    [Fact]
    public void SetDaqPtr_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.SetDaqPtrErrorResponse.Span);
        Assert.Equal(XcpError.OutOfRange, response.Code);
    }

    // ---- WRITE_DAQ ----

    [Fact]
    public void WriteDaq_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.WriteDaq(bitOffset: 0, entrySize: 2, addressExtension: 0x00, address: 0x00001000);

        Assert.True(
            XcpGoldenSamples.WriteDaqRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.WriteDaqRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void WriteDaq_positive_response_is_single_pid()
    {
        XcpResponseDecoder.WriteDaq(XcpGoldenSamples.WriteDaqPositiveResponse.Span);
    }

    [Fact]
    public void WriteDaq_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.WriteDaqErrorResponse.Span);
        Assert.Equal(XcpError.DaqActive, response.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void WriteDaq_rejects_invalid_entry_size(byte entrySize)
    {
        // XCP 1.0 WRITE_DAQ entrySize 合法范围 1..4（单条目 ≤4B，spec §1）。
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.WriteDaq(bitOffset: 0, entrySize, addressExtension: 0x00, address: 0x00001000));
    }

    // ---- CLEAR_DAQ_LIST ----

    [Fact]
    public void ClearDaqList_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.ClearDaqList(mode: 0x00, daqListNumber: 0);

        Assert.True(
            XcpGoldenSamples.ClearDaqListRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.ClearDaqListRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void ClearDaqList_positive_response_is_single_pid()
    {
        XcpResponseDecoder.ClearDaqList(XcpGoldenSamples.ClearDaqListPositiveResponse.Span);
    }

    [Fact]
    public void ClearDaqList_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.ClearDaqListErrorResponse.Span);
        Assert.Equal(XcpError.DaqActive, response.Code);
    }

    // ---- START_STOP_DAQ_LIST ----

    [Fact]
    public void StartStopDaqList_stop_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.StartStopDaqList(mode: 0x00, daqListNumber: 0);

        Assert.True(
            XcpGoldenSamples.StartStopDaqListStopRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.StartStopDaqListStopRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void StartStopDaqList_start_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.StartStopDaqList(mode: 0x01, daqListNumber: 0);

        Assert.True(
            XcpGoldenSamples.StartStopDaqListStartRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.StartStopDaqListStartRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void StartStopDaqList_positive_response_decodes_first_pid()
    {
        var response = XcpResponseDecoder.StartStopDaqList(XcpGoldenSamples.StartStopDaqListPositiveResponse.Span);

        Assert.Equal((byte)0x00, response.FirstPid);

        // 单字节钉死：firstPid 只取 response[1]，response[2] 保留位不得折叠进 PID。
        var synthetic = new byte[] { 0xFF, 0x2A, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var decoded = XcpResponseDecoder.StartStopDaqList(synthetic);
        Assert.Equal((byte)0x2A, decoded.FirstPid);
    }

    [Fact]
    public void StartStopDaqList_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.StartStopDaqListErrorResponse.Span);
        Assert.Equal(XcpError.OutOfRange, response.Code);
    }

    [Fact]
    public void StartStopDaqList_rejects_mode_2_select_path()
    {
        // spec §1 写死：仅 mode 0/1（select mode 2 + SYNCH 组合路径不使用）。
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.StartStopDaqList(mode: 0x02, daqListNumber: 0));
    }

    [Fact]
    public void StartStopDaqList_rejects_mode_3()
    {
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.StartStopDaqList(mode: 0x03, daqListNumber: 0));
    }

    // ---- START_STOP_SYNCH ----

    [Fact]
    public void StartStopSynch_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.StartStopSynch();

        Assert.True(
            XcpGoldenSamples.StartStopSynchRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.StartStopSynchRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void StartStopSynch_positive_response_is_single_pid()
    {
        XcpResponseDecoder.StartStopSynch(XcpGoldenSamples.StartStopSynchPositiveResponse.Span);
    }

    [Fact]
    public void StartStopSynch_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.StartStopSynchErrorResponse.Span);
        Assert.Equal(XcpError.CmdBusy, response.Code);
    }

    // ---- GET_DAQ_PROCESSOR_INFO ----

    [Fact]
    public void GetDaqProcessorInfo_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.GetDaqProcessorInfo();

        Assert.True(
            XcpGoldenSamples.GetDaqProcessorInfoRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.GetDaqProcessorInfoRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void GetDaqProcessorInfo_positive_response_decodes_fields()
    {
        var response = XcpResponseDecoder.GetDaqProcessorInfo(XcpGoldenSamples.GetDaqProcessorInfoPositiveResponse.Span);

        Assert.Equal((ushort)1, response.MaxDaq);
        Assert.Equal((ushort)1, response.MaxEventChannel);
        Assert.Equal(0, response.MinDaq);
        Assert.Equal(0x00, response.DaqKeyByte);
    }

    [Fact]
    public void GetDaqProcessorInfo_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.GetDaqProcessorInfoErrorResponse.Span);
        Assert.Equal(XcpError.CmdUnknown, response.Code);
    }

    // ---- GET_DAQ_RESOLUTION_INFO ----

    [Fact]
    public void GetDaqResolutionInfo_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.GetDaqResolutionInfo();

        Assert.True(
            XcpGoldenSamples.GetDaqResolutionInfoRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.GetDaqResolutionInfoRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void GetDaqResolutionInfo_positive_response_decodes_fields()
    {
        var response = XcpResponseDecoder.GetDaqResolutionInfo(XcpGoldenSamples.GetDaqResolutionInfoPositiveResponse.Span);

        Assert.Equal(1, response.GranularityDaq);
        Assert.Equal(4, response.MaxOdtEntrySizeDaq);
        Assert.Equal(0, response.GranularityStim);
        Assert.Equal(0, response.MaxOdtEntrySizeStim);
        Assert.Equal((byte)0x00, response.TimestampTicks);

        // 单字节钉死：timestampTicks 只取 response[5]，response[6..7] 保留位不得折叠。
        var synthetic = new byte[] { 0xFF, 0x01, 0x01, 0x00, 0x00, 0x07, 0x99, 0x66 };
        var decoded = XcpResponseDecoder.GetDaqResolutionInfo(synthetic);
        Assert.Equal((byte)0x07, decoded.TimestampTicks);
    }

    [Fact]
    public void GetDaqResolutionInfo_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.GetDaqResolutionInfoErrorResponse.Span);
        Assert.Equal(XcpError.CmdUnknown, response.Code);
    }

    // ---- GET_DAQ_LIST_INFO ----

    [Fact]
    public void GetDaqListInfo_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.GetDaqListInfo(daqListNumber: 0);

        Assert.True(
            XcpGoldenSamples.GetDaqListInfoRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.GetDaqListInfoRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void GetDaqListInfo_positive_response_decodes_fields()
    {
        var response = XcpResponseDecoder.GetDaqListInfo(XcpGoldenSamples.GetDaqListInfoPositiveResponse.Span);

        Assert.Equal(0x00, response.Mode);
        Assert.Equal(15, response.MaxOdt);
        Assert.Equal(1, response.MaxDaqList);
        Assert.Equal((ushort)0x0000, response.FirstPid);
    }

    [Fact]
    public void GetDaqListInfo_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.GetDaqListInfoErrorResponse.Span);
        Assert.Equal(XcpError.OutOfRange, response.Code);
    }

    // ---- GET_DAQ_EVENT_INFO ----

    [Fact]
    public void GetDaqEventInfo_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.GetDaqEventInfo(eventChannel: 0);

        Assert.True(
            XcpGoldenSamples.GetDaqEventInfoRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.GetDaqEventInfoRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void GetDaqEventInfo_positive_response_decodes_fields()
    {
        var response = XcpResponseDecoder.GetDaqEventInfo(XcpGoldenSamples.GetDaqEventInfoPositiveResponse.Span);

        Assert.Equal(0x40, response.EventChInfo);
        Assert.Equal(15, response.MaxDaqList);
        Assert.Equal((ushort)0, response.EventChannel);
        Assert.Equal((byte)0x0A, response.EventCycle);
        Assert.Equal((byte)0x06, response.EventChannelTimeUnit);
        Assert.Equal((byte)0x00, response.Priority);
    }

    [Fact]
    public void GetDaqEventInfo_time_unit_nonzero_response_decodes_fields()
    {
        // TIME_UNIT≠0 黄金样本：钉死 [5]=eventCycle、[6]=eventChannelTimeUnit、[7]=priority 三个独立单字节，不再折叠成 3B 字段。
        var response = XcpResponseDecoder.GetDaqEventInfo(XcpGoldenSamples.GetDaqEventInfoTimeUnitNonZeroPositiveResponse.Span);

        Assert.Equal((byte)0x05, response.EventCycle);
        Assert.Equal((byte)0x02, response.EventChannelTimeUnit);
        Assert.Equal((byte)0x01, response.Priority);
    }

    [Fact]
    public void GetDaqEventInfo_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.GetDaqEventInfoErrorResponse.Span);
        Assert.Equal(XcpError.OutOfRange, response.Code);
    }
}
