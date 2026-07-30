using PeakCan.Host.Infrastructure.Cli;

namespace PeakCan.Host.Cli.Tests;

public class ImportOdxCliTests
{
    private static string WriteTempOdx(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"odx_cli_{Guid.NewGuid():N}.odx");
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
              </POS-RESPONSE>
            </DIAG-COMM>
          </DIAG-COMM-SPEC>
        </ODX>
        """;

    [Fact]
    public void CliArgsParser_ParsesImportOdx_Arguments()
    {
        var odxPath = WriteTempOdx(MinimalOdx);
        try
        {
            var args = new[] { "--import-odx", odxPath, "--ecu-name", "BMS", "--import-uds-req", "0x7E0", "--import-uds-resp", "0x7E8" };
            var cli = CliArgsParser.Parse(args);

            Assert.Equal(odxPath, cli.ImportOdxPath);
            Assert.Equal("BMS", cli.ImportOdxEcuName);
            Assert.Equal(0x7E0u, cli.ImportOdxRequestId);
            Assert.Equal(0x7E8u, cli.ImportOdxResponseId);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }

    [Fact]
    public void CliArgsParser_ImportOdx_Defaults()
    {
        var odxPath = WriteTempOdx(MinimalOdx);
        try
        {
            var args = new[] { "--import-odx", odxPath };
            var cli = CliArgsParser.Parse(args);

            Assert.Equal(odxPath, cli.ImportOdxPath);
            Assert.Null(cli.ImportOdxEcuName);
            Assert.Equal(0x7E0u, cli.ImportOdxRequestId);
            Assert.Equal(0x7E8u, cli.ImportOdxResponseId);
        }
        finally
        {
            File.Delete(odxPath);
        }
    }
}
