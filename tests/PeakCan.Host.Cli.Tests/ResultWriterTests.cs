using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Cli;
using Xunit;

namespace PeakCan.Host.Cli.Tests;

public class ResultWriterTests
{
    private static TestCaseResult CreateCaseResult(string name, bool passed, int elapsedMs = 1000)
        => new(name, name, passed, passed ? null : "fail reason", elapsedMs,
            1, passed ? 1 : 0, passed ? 0 : 1, 0, 0,
            Array.Empty<StepResult>());

    [Fact]
    public async Task WriteTrx_valid_result_produces_valid_XML()
    {
        var result = new TestSuiteResult("TestSuite", 2, 1, 1, 0, 2000,
            Array.Empty<string>(),
            new[] { CreateCaseResult("case_1", true), CreateCaseResult("case_2", false) });

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);

            File.Exists(path).Should().BeTrue();
            var doc = XDocument.Load(path);
            doc.Root.Should().NotBeNull();
            doc.Root!.Name.LocalName.Should().Be("TestRun");
            doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").Should().HaveCount(2);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteTrx_passed_case_outcome_Passed()
    {
        var result = new TestSuiteResult("S", 1, 1, 0, 0, 1000,
            Array.Empty<string>(), new[] { CreateCaseResult("c1", true) });

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);
            var doc = XDocument.Load(path);
            var outcome = doc.Descendants().First(e => e.Name.LocalName == "UnitTestResult")
                .Attribute("outcome")!.Value;
            outcome.Should().Be("Passed");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteTrx_failed_case_outcome_Failed()
    {
        var result = new TestSuiteResult("S", 1, 0, 1, 0, 1000,
            Array.Empty<string>(), new[] { CreateCaseResult("c1", false) });

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);
            var doc = XDocument.Load(path);
            var outcome = doc.Descendants().First(e => e.Name.LocalName == "UnitTestResult")
                .Attribute("outcome")!.Value;
            outcome.Should().Be("Failed");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task End_to_end_HostBuilder_DI_pipeline_executes_test_suite()
    {
        // Create inline DBC + ASC fixtures
        const string dbc = """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU

            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """;

        const string asc = @"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  64 00 00 00 00 00 00 00
 0.100000 1  100  8  64 00 00 00 00 00 00 00
";

        var dbcPath = Path.Combine(Path.GetTempPath(), $"hil_cli_{Guid.NewGuid():N}.dbc");
        var ascPath = Path.Combine(Path.GetTempPath(), $"hil_cli_{Guid.NewGuid():N}.asc");
        File.WriteAllText(dbcPath, dbc, Encoding.UTF8);
        File.WriteAllText(ascPath, asc, Encoding.UTF8);

        try
        {
            // Build host via HeadlessHostBuilder (production DI configuration)
            var cli = new CliArgs(dbcPath, ascPath, "/dev/null");
            using var host = HeadlessHostBuilder.Build(cli);

            var engine = host.Services.GetRequiredService<TestSuiteEngine>();
            var channel = host.Services.GetRequiredService<ICanChannel>();
            var ctx = host.Services.GetRequiredService<Core.HIL.Contracts.IAssertionContext>();

            // Create and execute a test suite
            var suite = new TestSuite("CliIntegrationSuite",
                new[] { new TestCase("case_1", "Assert Signal", "", null,
                    new[] { TestCaseStep.Create(new AssertSignalStep("TestMsg.TestSignal", 100.0, 5.0)) },
                    null, Array.Empty<string>(), 0, null) },
                Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            // Wait for frames to be decoded
            await Task.Delay(500);

            var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

            result.TotalCases.Should().Be(1);
            result.CaseResults[0].Passed.Should().BeTrue("AssertSignal(100) should pass");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task WriteTrx_empty_suite_produces_valid_XML_with_no_results()
    {
        var result = new TestSuiteResult("Empty", 0, 0, 0, 0, 0,
            Array.Empty<string>(), Array.Empty<TestCaseResult>());

        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.trx");
        try
        {
            await ResultWriter.WriteTrx(result, path);
            var doc = XDocument.Load(path);
            doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
