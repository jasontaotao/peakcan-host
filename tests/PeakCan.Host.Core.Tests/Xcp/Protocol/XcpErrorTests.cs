using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// XcpError 枚举：逐值断言钉死线上错误码（XCP 1.0 Part 2 Table 8，spec §3 Protocol 命令清单涉及的码）。
/// </summary>
public class XcpErrorTests
{
    [Theory]
    [InlineData(nameof(XcpError.CmdSynch), 0x00)]
    [InlineData(nameof(XcpError.CmdBusy), 0x10)]
    [InlineData(nameof(XcpError.DaqActive), 0x11)]
    [InlineData(nameof(XcpError.PgmActive), 0x12)]
    [InlineData(nameof(XcpError.CmdUnknown), 0x20)]
    [InlineData(nameof(XcpError.CmdInvalid), 0x21)]
    [InlineData(nameof(XcpError.OutOfRange), 0x22)]
    [InlineData(nameof(XcpError.WriteProtected), 0x23)]
    [InlineData(nameof(XcpError.AccessLocked), 0x24)]
    [InlineData(nameof(XcpError.AccessDenied), 0x25)]
    [InlineData(nameof(XcpError.CalPageActive), 0x26)]
    [InlineData(nameof(XcpError.PageNotExist), 0x27)]
    [InlineData(nameof(XcpError.Generic), 0x28)]
    [InlineData(nameof(XcpError.Crc), 0x29)]
    [InlineData(nameof(XcpError.Sequence), 0x2A)]
    [InlineData(nameof(XcpError.ResourceTemporary), 0x2B)]
    public void Error_code_values_are_pinned(string name, byte expected)
    {
        Assert.Equal(expected, (byte)Enum.Parse<XcpError>(name));
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x2C)]
    [InlineData(0xFF)]
    public void Unknown_error_code_is_rejected_not_silently_mapped(byte unknownCode)
    {
        // 未知错误码不得静默映射为"成功"——解码侧必须显式拒绝。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => XcpResponseDecoder.Error(new byte[] { XcpPid.Error, unknownCode }));
    }
}
