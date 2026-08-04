using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

/// <summary>
/// Sprint 9 Inc 3: DID Read transitions + end-to-end ODX import.
/// </summary>
public class OdxToEcuScriptAdapterDidTests
{
    private static string CreateDidOdx()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_ReadVin" SHORT-NAME="Read_VIN">
                                <REQUEST-REF ID-REF="REQ_ReadVin"/>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_ReadVin">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID">
                                        <CODED-VALUE>34</CODED-VALUE>
                                    </PARAM>
                                    <PARAM SEMANTIC="ID">
                                        <CODED-VALUE>61840</CODED-VALUE>
                                    </PARAM>
                                </PARAMS>
                            </REQUEST>
                        </REQUESTS>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
    }

    [Fact]
    public void DidRead_ExtractDids_GeneratesDynamicDidReadoutTransition()
    {
        var odxXml = CreateDidOdx();
        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            var adapter = new OdxToEcuScriptAdapter();
            var transitions = adapter.Load(tempPath, out _);

            // Assert: 1 DID Read transition for 0xF190 (61840 decimal)
            var didTransitions = transitions.Where(t => t.ServiceId == 0x22).ToList();
            Assert.Single(didTransitions);

            var didT = didTransitions[0];
            Assert.Null(didT.FromState); // wildcard
            Assert.Equal(new byte[] { 0xFF, 0xFF }, didT.DataMask);
            Assert.Equal(new byte[] { 0xF1, 0x90 }, didT.DataPattern); // 61840 = 0xF190
            Assert.IsType<DynamicResponse>(didT.Response);
            Assert.Equal("DidReadout", ((DynamicResponse)didT.Response).GeneratorName);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void OdxEcuScriptImporter_EndToEnd_GeneratesStatesJson()
    {
        // Arrange: minimal ODX with DID and SecurityAccess
        var odxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx">
                <DIAG-LAYER-CONTAINER ID="L1">
                    <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
                        <DIAG-COMMS>
                            <DIAG-SERVICE ID="SES_ReadVin" SHORT-NAME="Read_VIN">
                                <REQUEST-REF ID-REF="REQ_ReadVin"/>
                            </DIAG-SERVICE>
                        </DIAG-COMMS>
                        <REQUESTS>
                            <REQUEST ID="REQ_ReadVin">
                                <PARAMS>
                                    <PARAM SEMANTIC="SERVICE-ID">
                                        <CODED-VALUE>34</CODED-VALUE>
                                    </PARAM>
                                    <PARAM SEMANTIC="ID">
                                        <CODED-VALUE>61840</CODED-VALUE>
                                    </PARAM>
                                </PARAMS>
                            </REQUEST>
                        </REQUESTS>
                    </DIAG-LAYER>
                </DIAG-LAYER-CONTAINER>
            </ODX>
            """;

        var tempPath = Path.GetTempFileName() + ".odx";
        File.WriteAllText(tempPath, odxXml);

        try
        {
            // Act: use OdxEcuScriptImporter to generate JSON
            var json = OdxEcuScriptImporter.ImportToJson(tempPath, "TestECU", 0x7E0, 0x7E8);

            // Assert: output contains "states" array and is parseable
            Assert.Contains("\"states\"", json);

            // Verify it can be parsed by EcuScriptLoader
            var script = EcuScriptLoader.Parse(json);
            Assert.Equal("TestECU", script.Name);
            Assert.NotNull(script.StateMachine);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
