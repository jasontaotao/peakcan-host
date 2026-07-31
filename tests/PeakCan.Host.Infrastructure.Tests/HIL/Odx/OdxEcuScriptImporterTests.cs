using System.Text.Json;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Odx;

public class OdxEcuScriptImporterTests
{
    private static string WriteTempOdx(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"odx_import_{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, content);
        return path;
    }

    // ODX with DID Read service (0x22) and proper namespace
    private const string DidOdx = """
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

    private const string EmptyOdx = """
        <?xml version="1.0" encoding="utf-8"?>
        <ODX xmlns="http://www.asam.net/xml/odx">
          <DIAG-LAYER-CONTAINER ID="L1">
            <DIAG-LAYER ID="ECU_Layer" SHORT-NAME="ECU">
              <DIAG-COMMS>
              </DIAG-COMMS>
            </DIAG-LAYER>
          </DIAG-LAYER-CONTAINER>
        </ODX>
        """;

    [Fact]
    public void ImportToJson_ExtractsServices_FromValidOdx()
    {
        var odxPath = WriteTempOdx(DidOdx);
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(odxPath, "BMS", 0x7E0, 0x7E8);

            Assert.NotNull(json);
            Assert.Contains("BMS", json);
            Assert.Contains("0x7E0", json);
            Assert.Contains("0x7E8", json);
            Assert.Contains("states", json); // states format, not rules
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void ImportToJson_GeneratesCorrectStatesFormat()
    {
        var odxPath = WriteTempOdx(DidOdx);
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(odxPath, "BMS", 0x7E0, 0x7E8);

            using var doc = JsonDocument.Parse(json);
            var states = doc.RootElement.GetProperty("states");
            Assert.True(states.GetArrayLength() >= 1, "Should have at least one state");

            var wildcardState = states[0];
            Assert.Equal("wildcard", wildcardState.GetProperty("name").GetString());

            var transitions = wildcardState.GetProperty("transitions");
            Assert.True(transitions.GetArrayLength() >= 1, "Should have at least one transition");

            // DID Read transition: serviceId=0x22, dataMask=[0xFF,0xFF], dataPattern=[0xF1,0x90]
            var didTransition = transitions[0];
            Assert.Equal("0x22", didTransition.GetProperty("serviceId").GetString());

            var dataMask = didTransition.GetProperty("dataMask");
            Assert.Equal(2, dataMask.GetArrayLength());
            Assert.Equal(0xFF, dataMask[0].GetByte());
            Assert.Equal(0xFF, dataMask[1].GetByte());

            var dataPattern = didTransition.GetProperty("dataPattern");
            Assert.Equal(0xF1, dataPattern[0].GetByte());
            Assert.Equal(0x90, dataPattern[1].GetByte());
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void ImportToJson_GeneratesCorrectCanIds()
    {
        var odxPath = WriteTempOdx(DidOdx);
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(odxPath, "BMS", 0x7E0, 0x7E8);

            using var doc = JsonDocument.Parse(json);
            var canIds = doc.RootElement.GetProperty("canIds");
            Assert.Equal("0x7E0", canIds.GetProperty("requestId").GetString());
            Assert.Equal("0x7E8", canIds.GetProperty("responseId").GetString());
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void ImportToJson_Throws_WhenNoServicesFound()
    {
        var odxPath = WriteTempOdx(EmptyOdx);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                OdxEcuScriptImporter.ImportToJson(odxPath, "Empty", 0x7E0, 0x7E8));
        }
        finally
        {
            File.Delete(odxPath);
        }
    }
}
