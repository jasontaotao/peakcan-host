using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// SYNCH 命令字节级黄金样本（spec §5 验收 1）：请求 8B 逐字节 + 正响应 + 负响应错误码。
/// </summary>
public class SynchCommandTests
{
    [Fact]
    public void Request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.Synch();

        Assert.True(
            XcpGoldenSamples.SynchRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.SynchRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void Positive_response_is_single_pid_byte()
    {
        // SYNCH 正响应仅 [FF]，无字段。
        XcpResponseDecoder.Synch(XcpGoldenSamples.SynchPositiveResponse.Span);
    }

    [Fact]
    public void ErrorResponse_decodes_error_code_from_golden_sample()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.SynchErrorResponse.Span);

        Assert.Equal(XcpError.CmdUnknown, response.Code);
    }
}
