using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// GET_COMM_MODE_INFO 命令字节级黄金样本（spec §5 验收 1）：请求 8B 逐字节 + 正响应逐字段 + 负响应错误码。
/// </summary>
public class GetCommModeInfoCommandTests
{
    [Fact]
    public void Request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.GetCommModeInfo();

        Assert.True(
            XcpGoldenSamples.GetCommModeInfoRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.GetCommModeInfoRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void Positive_response_decodes_fields_from_golden_sample()
    {
        var response = XcpResponseDecoder.GetCommModeInfo(XcpGoldenSamples.GetCommModeInfoPositiveResponse.Span);

        Assert.Equal(0x00, response.CommModeOptional);
        Assert.Equal(0x00, response.MaxBs);
        Assert.Equal(0x00, response.MinSt);
        Assert.Equal(0x000A, response.QueueSize);
    }

    [Fact]
    public void ErrorResponse_decodes_error_code_from_golden_sample()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.GetCommModeInfoErrorResponse.Span);

        Assert.Equal(XcpError.CmdUnknown, response.Code);
    }
}
