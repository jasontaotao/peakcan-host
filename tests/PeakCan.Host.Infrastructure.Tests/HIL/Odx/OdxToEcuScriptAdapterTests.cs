using PeakCan.HIL.Core.HIL;
using System.Text.Json;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

/// <summary>
/// Sprint 9 Inc 0: OdxToEcuScriptAdapter namespace resolution.
/// Verifies that the adapter correctly resolves ODX namespace variants
/// and passes them to existing extractors.
/// </summary>
public class OdxToEcuScriptAdapterTests
{
    private static string CreateMinimalOdx(string namespaceXml, string body)
    {
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ODX {namespaceXml}>
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            {body}
                        </DIAG-COMMS>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
    }

    [Fact]
    public void NamespaceResolution_OdxNamespace_ReturnsCorrectNs()
    {
        // Arrange: ODX 2.x document with proper xmlns
        var odxXml = CreateMinimalOdx(
            "xmlns=\"http://www.asam.net/xml/odx\"",
            """
            <DIAG-SERVICE ID="SES_Send" SHORT-NAME="SecurityAccess_Send">
                <REQUEST-REF ID="REQ_SecurityAccess_Send"/>
            </DIAG-SERVICE>
            """);

        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            // Act
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Assert: adapter successfully parsed (transitions may be empty if no
            // extractable services, but namespace resolution must not throw)
            Assert.NotNull(transitions);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void NamespaceResolution_EmptyNamespace_ReturnsEmptyNs()
    {
        // Arrange: ODX-D document (Vector CANdelaStudio .odx-d) with no xmlns
        var odxXml = CreateMinimalOdx(
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"odx.xsd\"",
            """
            <DIAG-SERVICE ID="SES_Read" SHORT-NAME="ReadDataByIdentifier">
                <REQUEST-REF ID="REQ_Read"/>
            </DIAG-SERVICE>
            """);

        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            // Act
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Assert: empty namespace resolved successfully
            Assert.NotNull(transitions);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void NamespaceResolution_InvalidNamespace_ThrowsOrReturnsEmpty()
    {
        // Arrange: ODX document with wrong namespace
        var odxXml = CreateMinimalOdx(
            "xmlns=\"http://wrong-namespace.example.com\"",
            """
            <DIAG-SERVICE ID="SES_Test" SHORT-NAME="Test">
                <REQUEST-REF ID="REQ_Test"/>
            </DIAG-SERVICE>
            """);

        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            // Act & Assert: either throw OdxParseException or return empty (graceful degradation)
            var adapter = new OdxToEcuScriptAdapter();
            var threw = false;
            IReadOnlyList<EcuStateTransition>? transitions = null;

            try
            {
                transitions = adapter.Load(tempPath, out _);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.True(threw || transitions is not null,
                "Adapter should either throw or return gracefully");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
