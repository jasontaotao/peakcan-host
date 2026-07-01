using FluentAssertions;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DtcDefinitionTests
{
    [Fact]
    public void DtcDefinition_StoresFieldsCorrectly()
    {
        // Arrange + Act
        var dtc = new DtcDefinition(
            Code: 0x123456,
            ShortName: "P0123",
            Description: "O2 sensor circuit malfunction",
            StatusMask: 0x2F);

        // Assert
        dtc.Code.Should().Be(0x123456);
        dtc.ShortName.Should().Be("P0123");
        dtc.Description.Should().Be("O2 sensor circuit malfunction");
        dtc.StatusMask.Should().Be(0x2F);
    }

    [Fact]
    public void DtcDefinition_RecordStruct_HasValueEquality()
    {
        // Arrange + Act
        var a = new DtcDefinition(0x1, "x", "y", 0);
        var b = new DtcDefinition(0x1, "x", "y", 0);

        // Assert
        a.Should().Be(b);
    }
}
