using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// CONNECT 命令字节级黄金样本（spec §5 验收 1）：请求 8B 逐字节 + 正响应逐字段 + 负响应错误码。
/// </summary>
public class ConnectCommandTests
{
    [Fact]
    public void Request_mode0_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.Connect();

        Assert.True(
            XcpGoldenSamples.ConnectRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.ConnectRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void Request_mode1_encodes_mode_byte_with_reserved_padding()
    {
        var frame = XcpCommandEncoder.Connect(mode: 0x01);

        Assert.Equal(XcpPid.Connect, frame.Bytes.Span[0]);
        Assert.Equal(0x01, frame.Bytes.Span[1]);
        for (var i = 2; i < XcpCtoFrame.MaxByteLength; i++)
            Assert.Equal(0x00, frame.Bytes.Span[i]);
    }

    [Fact]
    public void Positive_response_decodes_fields_from_golden_sample()
    {
        var response = XcpResponseDecoder.Connect(XcpGoldenSamples.ConnectPositiveResponse.Span);

        Assert.Equal(0x01, response.ProtocolVersion);
        Assert.Equal(0x01, response.TransportVersion);
        Assert.Equal(0x04, response.Resources);
        Assert.Equal(0x01, response.CommModeBasic);
    }

    [Fact]
    public void ErrorResponse_decodes_error_code_from_golden_sample()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.ConnectErrorResponse.Span);

        Assert.Equal(XcpError.OutOfRange, response.Code);
    }
}
