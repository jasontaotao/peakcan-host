using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class OdxDocumentTests
{
    [Fact]
    public void DiagService_StoresFieldsCorrectly()
    {
        // Arrange + Act
        var service = new DiagService(
            Id: "svc.read_vin",
            ShortName: "ReadDataByIdentifier_VIN",
            RequestRefId: "DOP.0xF190");

        // Assert
        service.Id.Should().Be("svc.read_vin");
        service.ShortName.Should().Be("ReadDataByIdentifier_VIN");
        service.RequestRefId.Should().Be("DOP.0xF190");
    }

    [Fact]
    public void DiagLayer_StoresIdShortNameAndServices()
    {
        // Arrange
        var service = new DiagService("svc.x", "ReadX", "DOP.x");

        // Act
        var layer = new DiagLayer(
            Id: "DL.base",
            ShortName: "BaseVariant",
            Services: new[] { service });

        // Assert
        layer.Id.Should().Be("DL.base");
        layer.ShortName.Should().Be("BaseVariant");
        layer.Services.Should().ContainSingle();
    }

    [Fact]
    public void OdxDocument_StoresVersionAndLayers()
    {
        // Arrange
        var layer = new DiagLayer("DL.x", "X", System.Array.Empty<DiagService>());

        // Act
        var doc = new OdxDocument(
            Version: "2.0.0",
            Layers: new[] { layer });

        // Assert
        doc.Version.Should().Be("2.0.0");
        doc.Layers.Should().ContainSingle();
    }
}
