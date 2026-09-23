using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Abstractions;

/// <summary>
/// XCP PID/命令码常量表：逐值断言钉死线上字节（XCP 1.0 Part 2 + spec 命令清单）。
/// </summary>
public class XcpPidTests
{
    [Theory]
    [InlineData(nameof(XcpPid.PositiveResponse), 0xFF)]
    [InlineData(nameof(XcpPid.Error), 0xFE)]
    [InlineData(nameof(XcpPid.DaqDtoFirst), 0x00)]
    public void Response_pid_values_are_pinned(string name, byte expected)
    {
        Assert.Equal(expected, Value(name));
    }

    [Theory]
    [InlineData(nameof(XcpPid.Connect), 0xFF)]
    [InlineData(nameof(XcpPid.Disconnect), 0xFE)]
    [InlineData(nameof(XcpPid.GetStatus), 0xFD)]
    [InlineData(nameof(XcpPid.Synch), 0xFC)]
    [InlineData(nameof(XcpPid.GetCommModeInfo), 0xFB)]
    [InlineData(nameof(XcpPid.SetMta), 0xF6)]
    [InlineData(nameof(XcpPid.Upload), 0xF5)]
    [InlineData(nameof(XcpPid.ShortUpload), 0xF4)]
    [InlineData(nameof(XcpPid.Download), 0xF0)]
    [InlineData(nameof(XcpPid.ClearDaqList), 0xE3)]
    [InlineData(nameof(XcpPid.SetDaqPtr), 0xE2)]
    [InlineData(nameof(XcpPid.WriteDaq), 0xE1)]
    [InlineData(nameof(XcpPid.StartStopDaqList), 0xDE)]
    [InlineData(nameof(XcpPid.StartStopSynch), 0xDD)]
    [InlineData(nameof(XcpPid.GetDaqEventInfo), 0xDA)]
    [InlineData(nameof(XcpPid.GetDaqListInfo), 0xD9)]
    [InlineData(nameof(XcpPid.GetDaqProcessorInfo), 0xD8)]
    [InlineData(nameof(XcpPid.GetDaqResolutionInfo), 0xD7)]
    public void Command_code_values_are_pinned(string name, byte expected)
    {
        Assert.Equal(expected, Value(name));
    }

    private static byte Value(string name)
        => (byte)(typeof(XcpPid).GetField(name)!.GetValue(null) ?? 0);
}
