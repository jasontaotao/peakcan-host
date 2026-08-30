using FluentAssertions;
using PeakCan.Host.App.Services.J1939;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.J1939;

public class J1939TpAnnotationTests
{
    [Theory]
    [InlineData(new byte[] { 0x20, 49, 0, 7, 0xFF, 0, 2, 0 }, 0x1CECFFF4, "TP.CM BAM PGN=0x000200 len=49 pkts=7")]
    // 计划缺陷修正：brief 原期望 "TP.DT #3/7"，但 L1 注解是无状态单帧 API
    //（Annotate(ReplayFrame)），TP.DT 帧本身只含序号+7 字节载荷，总包数仅存于
    // TP.CM（brief 注亦言 "/7 取 CM 总包数字段"）——单帧不可推导。按 brief 实现
    // 代码（绑定的格式串 $"TP.DT #{seq}"）期望改为 "TP.DT #3"。
    [InlineData(new byte[] { 0x03, 1, 2, 3, 4, 5, 6, 7 }, 0x1CEBFFF4, "TP.DT #3")]
    [InlineData(new byte[] { 0x10, 49, 0, 7, 0xFF, 0, 2, 0 }, 0x1CEC56F4, "TP.CM RTS PGN=0x000200 len=49 pkts=7")]
    [InlineData(new byte[] { 0x11, 7, 1, 0xFF, 0xFF, 0, 2, 0 }, 0x1CEC56F4, "TP.CM CTS next=1 grant=7")]
    [InlineData(new byte[] { 0x13, 49, 0, 7, 0xFF, 0, 2, 0 }, 0x1CEC56F4, "TP.CM EOM_ACK len=49 pkts=7")]
    [InlineData(new byte[] { 0xFF, 4, 0xFF, 0xFF, 0xFF, 0, 2, 0 }, 0x1CEC56F4, "TP.CM ABORT reason=4")]
    public void Annotates_Tp_Frames(byte[] data, uint id, string expected)
        => J1939TpAnnotation.Annotate(new(0.0, id, 8, data, HIL.Core.FrameFlags.None, true)).Should().Be(expected);

    [Fact]
    public void Returns_Null_For_Non_Tp_Frames()
        => J1939TpAnnotation.Annotate(new(0.0, 0x180256F4, 8, new byte[8], HIL.Core.FrameFlags.None, true)).Should().BeNull();
}
