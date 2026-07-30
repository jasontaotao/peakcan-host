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

    private const string MinimalOdx = """
        <?xml version="1.0" encoding="utf-8"?>
        <ODX xmlns="http://www.asam.net/xml/v2.2.0">
          <DIAG-COMM-SPEC>
            <DIAG-COMM ID="REQ_0x22">
              <REQUEST-REF ID-REF="SID_0x22" />
              <POS-RESPONSE ID="RESP_0x22">
                <PARAM>
                  <CODED-VALUE>98</CODED-VALUE>
                </PARAM>
                <PARAM>
                  <CODED-VALUE>241</CODED-VALUE>
                </PARAM>
                <PARAM>
                  <CODED-VALUE>144</CODED-VALUE>
                </PARAM>
              </POS-RESPONSE>
            </DIAG-COMM>
          </DIAG-COMM-SPEC>
        </ODX>
        """;

    private const string EmptyOdx = """
        <?xml version="1.0" encoding="utf-8"?>
        <ODX xmlns="http://www.asam.net/xml/v2.2.0">
          <DIAG-COMM-SPEC>
          </DIAG-COMM-SPEC>
        </ODX>
        """;

    [Fact]
    public void ImportToJson_ExtractsServices_FromValidOdx()
    {
        var odxPath = WriteTempOdx(MinimalOdx);
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(odxPath, "BMS", 0x7E0, 0x7E8);

            Assert.NotNull(json);
            Assert.Contains("BMS", json);
            Assert.Contains("0x7E0", json);
            Assert.Contains("0x7E8", json);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void ImportToJson_GeneratesCorrectResponseData()
    {
        var odxPath = WriteTempOdx(MinimalOdx);
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(odxPath, "BMS", 0x7E0, 0x7E8);

            // Parse the JSON to verify responseData bytes
            using var doc = JsonDocument.Parse(json);
            var rules = doc.RootElement.GetProperty("rules");
            Assert.True(rules.GetArrayLength() >= 1, "Should have at least one rule");

            var firstRule = rules[0];
            var responseData = firstRule.GetProperty("responseData");
            Assert.Equal(3, responseData.GetArrayLength());
            Assert.Equal(98, responseData[0].GetByte());
            Assert.Equal(241, responseData[1].GetByte());
            Assert.Equal(144, responseData[2].GetByte());
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void ImportToJson_GeneratesCorrectCanIds()
    {
        var odxPath = WriteTempOdx(MinimalOdx);
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
