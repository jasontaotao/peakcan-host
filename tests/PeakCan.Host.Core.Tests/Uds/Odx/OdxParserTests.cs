using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class OdxParserTests
{
    [Fact]
    public void Parse_XDocumentWithBaseVariant_ReturnsOdxDocument()
    {
        // Arrange — minimal valid ODX 2.0.0 with one BASE-VARIANT layer
        // containing one DIAG-SERVICE.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.x">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant">
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.x" SHORT-NAME="ReadX">
                      <REQUEST-REF ID-REF="DOP.x"/>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
        var xdoc = XDocument.Parse(xml);

        // Act
        var parser = new OdxParser();
        var doc = parser.Parse(xdoc, out var warnings);

        // Assert
        doc.Version.Should().Be("2.0.0");
        doc.Layers.Should().ContainSingle();
        doc.Layers[0].Id.Should().Be("DL.base");
        doc.Layers[0].ShortName.Should().Be("BaseVariant");
        doc.Layers[0].Services.Should().ContainSingle();
        doc.Layers[0].Services[0].Id.Should().Be("svc.x");
        doc.Layers[0].Services[0].RequestRefId.Should().Be("DOP.x");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyContainer_ReturnsDocumentWithEmptyLayerList()
    {
        // Arrange
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.empty"/>
            </ODX>
            """;
        var xdoc = XDocument.Parse(xml);

        // Act
        var doc = new OdxParser().Parse(xdoc, out _);

        // Assert
        doc.Version.Should().Be("2.0.0");
        doc.Layers.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnsupportedVersion_AddsWarningButStillProduces()
    {
        // Arrange — version 5.0.0 is outside accepted 2.0.0 + 2.2.0 range.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="5.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.x">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="BaseVariant"/>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
        var xdoc = XDocument.Parse(xml);

        // Act
        var doc = new OdxParser().Parse(xdoc, out var warnings);

        // Assert — non-fatal: warning + still produces.
        warnings.Should().Contain(w => w.Contains("5.0.0"));
        doc.Version.Should().Be("5.0.0");
    }
}
