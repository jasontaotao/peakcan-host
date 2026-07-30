using PeakCan.Host.Core.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilRunRequestTests
{
    [Fact]
    public void ToCliArgs_TraceMode_MapsCorrectly()
    {
        // Arrange
        var request = new HilRunRequest("x.dbc", "y.json", TracePath: "x.asc");

        // Act
        var cli = Infrastructure.HIL.HilRunRequestExtensions.ToCliArgs(request);

        // Assert
        Assert.Equal("x.asc", cli.TracePath);
        Assert.Null(cli.HardwareChannel);
    }

    [Fact]
    public void ToCliArgs_HardwareMode_MapsCorrectly()
    {
        // Arrange
        var request = new HilRunRequest("x.dbc", "y.json", HardwareChannel: "USB1");

        // Act
        var cli = Infrastructure.HIL.HilRunRequestExtensions.ToCliArgs(request);

        // Assert
        Assert.Equal("USB1", cli.HardwareChannel);
        Assert.Null(cli.TracePath);
    }

    [Fact]
    public void ToCliArgs_UdsIds_Preserved()
    {
        // Arrange
        var request = new HilRunRequest("x.dbc", "y.json", HardwareChannel: "USB1",
            UdsRequestId: 0x714, UdsResponseId: 0x760);

        // Act
        var cli = Infrastructure.HIL.HilRunRequestExtensions.ToCliArgs(request);

        // Assert
        Assert.Equal(0x714u, cli.UdsRequestId);
        Assert.Equal(0x760u, cli.UdsResponseId);
    }
}
