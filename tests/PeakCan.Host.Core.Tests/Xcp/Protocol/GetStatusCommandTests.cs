using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// GET_STATUS 命令字节级黄金样本（spec §5 验收 1）：请求 8B 逐字节 + 正响应逐字段 + 负响应错误码。
/// </summary>
public class GetStatusCommandTests
{
    [Fact]
    public void Request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.GetStatus();

        Assert.True(
            XcpGoldenSamples.GetStatusRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.GetStatusRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void Positive_response_decodes_fields_from_golden_sample()
    {
        var response = XcpResponseDecoder.GetStatus(XcpGoldenSamples.GetStatusPositiveResponse.Span);

        Assert.Equal(0x02, response.SessionStatus);
        Assert.Equal(0x00, response.ProtectionStatus);
        Assert.Equal(0x01, response.MaxDaq);
    }

    [Fact]
    public void ErrorResponse_decodes_error_code_from_golden_sample()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.GetStatusErrorResponse.Span);

        Assert.Equal(XcpError.CmdBusy, response.Code);
    }
}
