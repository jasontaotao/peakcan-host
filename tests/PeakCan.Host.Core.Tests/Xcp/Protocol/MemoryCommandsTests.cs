using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// SET_MTA / UPLOAD / SHORT_UPLOAD / DOWNLOAD 字节级黄金样本（spec §5 验收 1）。
/// CTO 8B 约束：超限参数必须抛 ArgumentException（一帧一 CTO）。
/// DOWNLOAD 编解码实现、调度禁用（spec 决策 D2）。
/// </summary>
public class MemoryCommandsTests
{
    // ---- SET_MTA ----

    [Fact]
    public void SetMta_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.SetMta(addressExtension: 0x00, address: 0x00001000);

        Assert.True(
            XcpGoldenSamples.SetMtaRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.SetMtaRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void SetMta_positive_response_is_single_pid()
    {
        XcpResponseDecoder.SetMta(XcpGoldenSamples.SetMtaPositiveResponse.Span);
    }

    [Fact]
    public void SetMta_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.SetMtaErrorResponse.Span);
        Assert.Equal(XcpError.OutOfRange, response.Code);
    }

    // ---- UPLOAD ----

    [Fact]
    public void Upload_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.Upload(numberOfBytes: 4);

        Assert.True(
            XcpGoldenSamples.UploadRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.UploadRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void Upload_positive_response_decodes_data_from_golden_sample()
    {
        var data = XcpResponseDecoder.Upload(XcpGoldenSamples.UploadPositiveResponse.Span);

        Assert.Equal(4, data.Length);
        Assert.Equal(0x12, data[0]);
        Assert.Equal(0x34, data[1]);
        Assert.Equal(0x56, data[2]);
        Assert.Equal(0x78, data[3]);
    }

    [Fact]
    public void Upload_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.UploadErrorResponse.Span);
        Assert.Equal(XcpError.Sequence, response.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Upload_rejects_byte_count_outside_cto_limit(byte numberOfBytes)
    {
        // CTO 8B − PID 1B = 最大 7 字节数据；0 或 8 均超限。
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.Upload(numberOfBytes));
    }

    // ---- SHORT_UPLOAD ----

    [Fact]
    public void ShortUpload_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.ShortUpload(numberOfBytes: 2, address: 0x00002000, addressExtension: 0x00);

        // 逐字节钉死新格式：[F4, 00, 00, nbytes, addrExt, addr 低 3B LE]——
        // XCP on CAN 8B 装得下，addrExt 字节必须存在（A2L ADDRESS_EXTENSION_FREE=0 时恒 0 也要传）。
        Assert.Equal((byte)0xF4, frame.Bytes.Span[0]);
        Assert.Equal((byte)0x00, frame.Bytes.Span[1]);
        Assert.Equal((byte)0x00, frame.Bytes.Span[2]);
        Assert.Equal((byte)0x02, frame.Bytes.Span[3]);
        Assert.Equal((byte)0x00, frame.Bytes.Span[4]);
        Assert.Equal((byte)0x00, frame.Bytes.Span[5]);
        Assert.Equal((byte)0x20, frame.Bytes.Span[6]);
        Assert.Equal((byte)0x00, frame.Bytes.Span[7]);
        Assert.True(
            XcpGoldenSamples.ShortUploadRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.ShortUploadRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void ShortUpload_positive_response_decodes_data_from_golden_sample()
    {
        var data = XcpResponseDecoder.ShortUpload(XcpGoldenSamples.ShortUploadPositiveResponse.Span);

        Assert.Equal(2, data.Length);
        Assert.Equal(0xAB, data[0]);
        Assert.Equal(0xCD, data[1]);
    }

    [Fact]
    public void ShortUpload_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.ShortUploadErrorResponse.Span);
        Assert.Equal(XcpError.OutOfRange, response.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void ShortUpload_rejects_byte_count_outside_cto_limit(byte numberOfBytes)
    {
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.ShortUpload(numberOfBytes, address: 0x00001000, addressExtension: 0x00));
    }

    [Fact]
    public void ShortUpload_rejects_address_above_24bit()
    {
        // 地址高 8 位非零必须改走 ADDR_EXT，编码器不得静默截断。
        Assert.Throws<ArgumentException>(
            () => XcpCommandEncoder.ShortUpload(numberOfBytes: 2, address: 0x01000000, addressExtension: 0x00));
    }

    // ---- DOWNLOAD（编解码实现、调度禁用——spec D2）----

    [Fact]
    public void Download_request_matches_golden_sample()
    {
        var frame = XcpCommandEncoder.Download(data: new byte[] { 0xAB, 0xCD });

        Assert.True(
            XcpGoldenSamples.DownloadRequest.Span.SequenceEqual(frame.Bytes.Span),
            $"Expected {BitConverter.ToString(XcpGoldenSamples.DownloadRequest.ToArray())}, got {BitConverter.ToString(frame.Bytes.ToArray())}");
    }

    [Fact]
    public void Download_positive_response_is_single_pid()
    {
        XcpResponseDecoder.Download(XcpGoldenSamples.DownloadPositiveResponse.Span);
    }

    [Fact]
    public void Download_error_response_decodes_code()
    {
        var response = XcpResponseDecoder.Error(XcpGoldenSamples.DownloadErrorResponse.Span);
        Assert.Equal(XcpError.WriteProtected, response.Code);
    }

    [Fact]
    public void Download_rejects_data_exceeding_cto_payload()
    {
        // CTO 8B − 4B header (PID + blockMode + reserved + nbytes) = 最大 4B 数据。
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.Download(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }));
    }

    [Fact]
    public void Download_rejects_empty_data()
    {
        Assert.Throws<ArgumentException>(() => XcpCommandEncoder.Download(Array.Empty<byte>()));
    }

    // ---- 静态守卫：编码器无 SHORT_DOWNLOAD（spec 变更记录纠正为 SHORT_UPLOAD）----

    [Fact]
    public void Encoder_does_not_expose_short_download()
    {
        var methodNames = typeof(XcpCommandEncoder)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.DeclaringType == typeof(XcpCommandEncoder) && !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("ShortDownload", methodNames);
    }
}
