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

        // ASAM XCP Part 1 CONNECT 正响应标准布局（与 Xcp_Std.c:191-198 一致）：
        // [FF, RESSOURCE, COMM_MODE_BASIC, MAX_CTO, MAX_DTO(LSB,MSB), PROTOCOL_VERSION, TRANSPORT_VERSION]。
        Assert.Equal(0x01, response.ProtocolVersion);
        Assert.Equal(0x01, response.TransportVersion);
        Assert.Equal(0x04, response.Resources);
        Assert.Equal(0x01, response.CommModeBasic);
        Assert.Equal(0x08, response.MaxCto);
        Assert.Equal(0x0040, response.MaxDto);
    }

    [Fact]
    public void Positive_response_decodes_all_fields_from_asam_standard_layout()
    {
        // ASAM 标准布局全字段钉死：byte1=RESSOURCE、byte2=COMM_MODE_BASIC、byte3=MAX_CTO、
        // byte4-5=MAX_DTO（LE）、byte6=PROTOCOL_VERSION、byte7=TRANSPORT_VERSION。
        // 早先“真机非标准 CONNECT”是评审口径错误——Xcp_Std.c 布局即 ASAM 标准（round-3 修正）。
        var response = new byte[] { 0xFF, 0x07, 0x03, 0x08, 0x40, 0x00, 0x01, 0x01 };

        var connect = XcpResponseDecoder.Connect(response);

        Assert.Equal(0x07, connect.Resources);
        Assert.Equal(0x03, connect.CommModeBasic);
        Assert.Equal(0x08, connect.MaxCto);
        Assert.Equal(0x0040, connect.MaxDto);
        Assert.Equal(0x01, connect.ProtocolVersion);
        Assert.Equal(0x01, connect.TransportVersion);
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
