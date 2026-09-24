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
        // 黄金样本 byte5-7 在标准布局下是 MAX_CTO/MAX_DTO——黄金样本按 T6 时代注释生成，
        // 这三个字节为 0；值本身由下一条测试用标准样本断言，此处只钉字段语义。
        Assert.Equal(0x00, response.MaxCto);
        Assert.Equal(0x0000, response.MaxDto);
    }

    [Fact]
    public void Positive_response_decodes_max_cto_and_max_dto_from_standard_bytes_5_to_7()
    {
        // ASAM 标准布局：byte5=MAX_CTO、byte6-7=MAX_DTO（LE）——S2-T8 评审 Important-2 钉死。
        var response = new byte[] { 0xFF, 0x01, 0x01, 0x04, 0x01, 0x08, 0x08, 0x00 };

        var connect = XcpResponseDecoder.Connect(response);

        Assert.Equal(0x08, connect.MaxCto);
        Assert.Equal(0x0008, connect.MaxDto);
    }

    [Fact]
    public void Positive_response_shorter_than_8_bytes_throws()
    {
        // 标准布局 8B 起才有完整 MAX_CTO/MAX_DTO；<8B 一律拒绝（不静默截断）。
        Assert.Throws<ArgumentException>(
            () => XcpResponseDecoder.Connect(new byte[] { 0xFF, 0x01, 0x01, 0x04, 0x01, 0x08, 0x08 }));
    }

    [Fact]
    public void ErrorResponse_decodes_error_code_from_golden_sample()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.ConnectErrorResponse.Span);

        Assert.Equal(XcpError.OutOfRange, response.Code);
    }
}
