using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Cli.Tests;

public class ParseChannelHandleTests
{
    [Fact]
    public void ParseChannelHandle_USB1_Returns0x51()
    {
        // Act
        var handle = HeadlessHostBuilder.ParseChannelHandle("USB1");

        // Assert
        Assert.Equal((ushort)0x51, handle);
    }

    [Fact]
    public void ParseChannelHandle_USB16_Returns0x60()
    {
        // Act
        var handle = HeadlessHostBuilder.ParseChannelHandle("USB16");

        // Assert
        Assert.Equal((ushort)0x60, handle);
    }

    [Fact]
    public void ParseChannelHandle_Invalid_Throws()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => HeadlessHostBuilder.ParseChannelHandle("PCI1"));
        Assert.Contains("USB1..USB16", ex.Message);
    }
}
