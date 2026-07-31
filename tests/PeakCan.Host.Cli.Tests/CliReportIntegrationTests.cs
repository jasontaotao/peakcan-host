using System.Text.Json;
using PeakCan.Host.Infrastructure.Cli;
using Xunit;

namespace PeakCan.Host.Cli.Tests;

[Collection("CliProgram")]
public class CliReportIntegrationTests
{
    private static readonly string[] BaseArgs = { "--dbc", "x.dbc", "--suite", "y.json", "--trace", "x.asc" };

    private static string[] NormalArgs(params string[] extra)
        => extra.Length == 0 ? BaseArgs : BaseArgs.Concat(extra).ToArray();

    // --- Inc 0: CliArgs parser ---

    [Fact]
    public void CliArgsParser_ExportFramesDir_ParsesFlag()
    {
        var cli = CliArgsParser.Parse(NormalArgs("--export-frames", "/tmp/out"));
        Assert.Equal("/tmp/out", cli.ExportFramesDir);
    }

    [Fact]
    public void CliArgsParser_FormatHtml_ParsesFormat()
    {
        var cli = CliArgsParser.Parse(NormalArgs("--format", "html"));
        Assert.Equal("html", cli.Format);
    }

    [Fact]
    public void CliArgsParser_FormatHtmlJunit_ParsesFormat()
    {
        var cli = CliArgsParser.Parse(NormalArgs("--format", "html+junit"));
        Assert.Equal("html+junit", cli.Format);
    }

    // --- Inc 1: Program report switch + frame export ---

    /// <summary>Write minimal DBC + ECU script + suite to temp files.</summary>
    private static (string Dbc, string Ecu, string Suite) WriteTempE2E(bool failingCase)
    {
        var dbc = Path.Combine(Path.GetTempPath(), $"cli_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(dbc, """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);

        var ecu = Path.Combine(Path.GetTempPath(), $"cli_{Guid.NewGuid():N}.json");
        File.WriteAllText(ecu, """
            {
              "name": "TestEcu",
              "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
              "rules": [
                { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] }
              ]
            }
            """);

        var suite = Path.Combine(Path.GetTempPath(), $"cli_{Guid.NewGuid():N}.json");
        // Passing suite: sendFrame 0x3E -> expect 0x7E (0x7E0/0x7E8 IDs).
        // Failing suite appends an expectFrame that never matches (no such ECU rule).
        File.WriteAllText(suite, failingCase
            ? """
            {
              "name": "CliE2ESuite",
              "globalCaseFixtureKeys": [],
              "suiteFixtureKeys": [],
              "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
              "cases": [
                { "id": "c1", "name": "Pass", "steps": [
                    { "parameters": { "$kind": "sendFrame", "id": { "raw": 2016, "format": "Standard", "type": "Data" }, "data": [2, 62, 0], "fd": false } },
                    { "parameters": { "$kind": "expectFrame", "id": { "raw": 2024, "format": "Standard", "type": "Data" }, "dataMask": [0, 126], "timeoutMs": 2000 } }
                ] },
                { "id": "c2", "name": "Fail", "steps": [
                    { "parameters": { "$kind": "sendFrame", "id": { "raw": 2016, "format": "Standard", "type": "Data" }, "data": [2, 62, 0], "fd": false } },
                    { "parameters": { "$kind": "expectFrame", "id": { "raw": 2024, "format": "Standard", "type": "Data" }, "dataMask": [255, 255], "timeoutMs": 300 } }
                ] }
              ]
            }
            """
            : """
            {
              "name": "CliE2ESuite",
              "globalCaseFixtureKeys": [],
              "suiteFixtureKeys": [],
              "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
              "cases": [
                { "id": "c1", "name": "Pass", "steps": [
                    { "parameters": { "$kind": "sendFrame", "id": { "raw": 2016, "format": "Standard", "type": "Data" }, "data": [2, 62, 0], "fd": false } },
                    { "parameters": { "$kind": "expectFrame", "id": { "raw": 2024, "format": "Standard", "type": "Data" }, "dataMask": [0, 126], "timeoutMs": 2000 } }
                ] }
              ]
            }
            """);

        return (dbc, ecu, suite);
    }

    [Fact]
    public async Task Program_ConsoleFormat_OutputsSummary()
    {
        var (dbc, ecu, suite) = WriteTempE2E(failingCase: false);
        try
        {
            var originalOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                var exit = await Program.Main(new[]
                {
                    "--dbc", dbc, "--ecu", ecu, "--suite", suite, "--format", "console"
                });
                Assert.Equal(0, exit);
                var output = sw.ToString();
                Assert.Contains("Suite:", output);      // ConsoleSummaryFormatter header
                Assert.Contains("Total:", output);
                Assert.Contains("ALL PASSED", output);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
        finally
        {
            File.Delete(dbc); File.Delete(ecu); File.Delete(suite);
        }
    }

    [Fact]
    public async Task Program_HtmlFormat_WritesHtmlFile()
    {
        var (dbc, ecu, suite) = WriteTempE2E(failingCase: false);
        var report = Path.Combine(Path.GetTempPath(), $"cli_{Guid.NewGuid():N}.html");
        try
        {
            var originalOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                var exit = await Program.Main(new[]
                {
                    "--dbc", dbc, "--ecu", ecu, "--suite", suite, "--format", "html", "--output", report
                });
                Assert.Equal(0, exit);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.True(File.Exists(report), $"HTML report not written: {report}");
            var html = await File.ReadAllTextAsync(report);
            Assert.Contains("<html", html);
            Assert.Contains("HIL Test Report", html);
        }
        finally
        {
            File.Delete(dbc); File.Delete(ecu); File.Delete(suite);
            if (File.Exists(report)) File.Delete(report);
        }
    }

    [Fact]
    public async Task Program_ExportFrames_CreatesDirectory()
    {
        var (dbc, ecu, suite) = WriteTempE2E(failingCase: true);
        var framesDir = Path.Combine(Path.GetTempPath(), $"cli_frames_{Guid.NewGuid():N}");
        try
        {
            var originalOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                // Exit code 1 expected: the failing case makes AllPassed false.
                var exit = await Program.Main(new[]
                {
                    "--dbc", dbc, "--ecu", ecu, "--suite", suite, "--export-frames", framesDir
                });
                Assert.Equal(1, exit);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.True(Directory.Exists(framesDir), $"Frames dir not created: {framesDir}");
            var files = Directory.GetFiles(framesDir, "*.asc");
            Assert.NotEmpty(files);
        }
        finally
        {
            File.Delete(dbc); File.Delete(ecu); File.Delete(suite);
            if (Directory.Exists(framesDir)) Directory.Delete(framesDir, recursive: true);
        }
    }
}
