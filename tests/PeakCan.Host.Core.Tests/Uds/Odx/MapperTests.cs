using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class DidDopMappingTests
{
    [Fact]
    public void TryMap_ValidDidDop_ReturnsDefinition()
    {
        // Arrange — minimal DOP-BASE with id 0xF190.
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx" ID="DOP.0xF190" SHORT-NAME="VIN_DOP">
              <DIAG-CODED-TYPE BASE-TYPE="A_ASCIISTRING" BASE-DATA-TYPE="A_ASCIISTRING"/>
            </DOP-BASE>
            """);

        // Act
        var result = DidDop.TryMap(dop, out var warning);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(0xF190);
        warning.Should().BeNull();
    }

    [Fact]
    public void TryMap_NonExistentHexId_ReturnsNull()
    {
        // Arrange — id attribute missing
        var dop = XElement.Parse("""
            <DOP-BASE xmlns="http://www.asam.net/xml/odx"/>
            """);

        // Act
        var result = DidDop.TryMap(dop, out var warning);

        // Assert
        result.Should().BeNull();
        warning.Should().NotBeNull();
    }
}

public class DtcDopMappingTests
{
    [Fact]
    public void TryMap_ValidDtcDop_ReturnsDefinition()
    {
        // Arrange
        var dop = XElement.Parse("""
            <DTC-DOP xmlns="http://www.asam.net/xml/odx" ID="DTC.P0123" SHORT-NAME="P0123_O2">
              <DTC>
                <TROUBLE-CODE>0x012356</TROUBLE-CODE>
                <TEXT>
                  <DTC-TAB SHORT-NAME="DESC">O2 sensor circuit malfunction</DTC-TAB>
                </TEXT>
              </DTC>
            </DTC-DOP>
            """);

        // Act
        var result = DtcDop.TryMap(dop, out var warning);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Code.Should().Be(0x012356);
        result.Value.ShortName.Should().Be("P0123_O2");
        warning.Should().BeNull();
    }
}

public class EcuJobMappingTests
{
    [Fact]
    public void TryMap_ValidEcuJob_ReturnsRoutineDefinition()
    {
        // Arrange
        var job = XElement.Parse("""
            <ECU-JOB xmlns="http://www.asam.net/xml/odx" ID="JOB.0x1234" SHORT-NAME="eraseMemory">
              <DIAG-COMMS>
                <DIAG-SERVICE ID="svc.erase" SHORT-NAME="RoutineControl">
                  <REQUEST-REF ID-REF="DOP.0x1234"/>
                </DIAG-SERVICE>
              </DIAG-COMMS>
            </ECU-JOB>
            """);

        // Act
        var result = EcuJob.TryMap(job, out var warning);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(0x1234);
        result.Name.Should().Be("eraseMemory");
        warning.Should().BeNull();
    }
}
