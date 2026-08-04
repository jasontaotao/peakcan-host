using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilRunRequestTests
{
    [Fact]
    public void ToCliArgs_TraceMode_MapsCorrectly()
    {
        // Arrange
        var request = new HilRunRequest("x.dbc", "y.json", TracePath: "x.asc", Mode: HilMode.TraceReplay);

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
        var request = new HilRunRequest("x.dbc", "y.json", HardwareChannel: "USB1", Mode: HilMode.Hardware);

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
            UdsRequestId: 0x714, UdsResponseId: 0x760, Mode: HilMode.Hardware);

        // Act
        var cli = Infrastructure.HIL.HilRunRequestExtensions.ToCliArgs(request);

        // Assert
        Assert.Equal(0x714u, cli.UdsRequestId);
        Assert.Equal(0x760u, cli.UdsResponseId);
    }

    // --- Sprint 12: Mode-based mapping tests ---

    [Fact]
    public void ToCliArgs_VirtualEcuMode_MapsEcuScriptPath()
    {
        var request = new HilRunRequest("x.dbc", "y.json", EcuScriptPath: "ecu.json", Mode: HilMode.VirtualEcu);
        var cli = Infrastructure.HIL.HilRunRequestExtensions.ToCliArgs(request);

        Assert.Equal("ecu.json", cli.EcuScriptPath);
        Assert.Null(cli.TracePath);
        Assert.Null(cli.HardwareChannel);
    }

    [Fact]
    public void ToCliArgs_MatrixMode_MapsMatrixPath()
    {
        var request = new HilRunRequest("x.dbc", "y.json", MatrixPath: "matrix.json", Mode: HilMode.Matrix);
        var cli = Infrastructure.HIL.HilRunRequestExtensions.ToCliArgs(request);

        Assert.Equal("matrix.json", cli.MatrixPath);
        Assert.Null(cli.TracePath);
        Assert.Null(cli.EcuScriptPath);
    }
}
