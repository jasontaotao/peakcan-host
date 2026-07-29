using System.Globalization;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Sprint 2 Inc 4: Integration tests for the full HIL pipeline.
/// Uses inline DBC + ASC fixtures (self-contained).
/// </summary>
public class HILIntegrationTests
{
    /// <summary>Minimal DBC with one standard message (ID=256) and one signal.</summary>
    private const string SimpleDBC = """
        VERSION "1.0";
        NS_ :
        BS_:
        BU_: ECU

        BO_ 256 TestMsg: 8 ECU
         SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
        """;

    /// <summary>DBC with extended frame message (ID=0x98FEF100 with bit 31 set = 2566844672).</summary>
    private const string ExtendedDBC = """
        VERSION "1.0";
        NS_ :
        BS_:
        BU_: ECU

        BO_ 2566844672 ExtMsg: 8 ECU
         SG_ ExtSig : 0|8@1+ (1,0) [0|255] "V"  ECU
        """;

    private static string WriteTempAsc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_int_{Guid.NewGuid():N}.asc");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string WriteTempDbc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_int_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static TestSuite CreateSuite(params TestCase[] cases)
        => new("IntegrationSuite", cases, Array.Empty<string>(),
            Array.Empty<string>(), new TestSuiteConfig(), 0);

    private static TestCase CreateCase(string id, string name, params TestCaseStep[] steps)
        => new(id, name, "", null, steps, null, Array.Empty<string>(), 0, null);

    [Fact]
    public async Task End_to_end_standard_frame_signal_decoded_and_asserted()
    {
        // Arrange: DBC + ASC + Suite
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  64 00 00 00 00 00 00 00
 0.100000 1  100  8  64 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            doc.IsSuccess.Should().BeTrue("DBC should parse");

            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            using var ctx = new HILAssertionContext(channel, dbc);

            // Build real executor chain (mirrors HeadlessHostBuilder)
            var primitives = new AssertionPrimitives(ctx);
            var executors = new Core.HIL.StepExecutor.IStepExecutor[]
            {
                new WaitForSignalStepExecutor(primitives),
                new AssertSignalStepExecutor(primitives),
            };

            var engine = new TestSuiteEngine(new FakeFixtureResolver(), executors);

            var suite = CreateSuite(CreateCase("case_1", "Standard Frame Test",
                TestCaseStep.Create(new WaitForSignalStep("TestMsg.TestSignal", 100.0, 5.0, 5000)),
                TestCaseStep.Create(new AssertSignalStep("TestMsg.TestSignal", 100.0, 5.0))));

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

            result.TotalCases.Should().Be(1);
            result.CaseResults[0].Passed.Should().BeTrue("WaitForSignal(100) + AssertSignal(100) should pass");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task End_to_end_extended_frame_signal_decoded_correctly()
    {
        var dbcPath = WriteTempDbc(ExtendedDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  18FEF100  8  42 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            doc.IsSuccess.Should().BeTrue("DBC should parse");

            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            using var ctx = new HILAssertionContext(channel, dbc);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
            await Task.Delay(500);

            // Extended frame: CanFrame.Id.Raw=0x18FEF100 → DBC key 0x98FEF100
            var value = ctx.GetSignalValue("ExtMsg.ExtSig");
            value.Should().Be(0x42, "extended frame signal should be decoded via ToDbcLookupKey");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task End_to_end_Dispose_cleans_up_without_hanging()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  01 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            var ctx = new HILAssertionContext(channel, dbc);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
            await Task.Delay(200);

            // Dispose should complete within 5s
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Dispose();
            await channel.DisposeAsync();
            sw.ElapsedMilliseconds.Should().BeLessThan(5000, "Dispose should complete within 5s");
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task End_to_end_multiple_frames_signal_cache_updates()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  10 00 00 00 00 00 00 00
 0.100000 1  100  8  20 00 00 00 00 00 00 00
 0.200000 1  100  8  30 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            using var ctx = new HILAssertionContext(channel, dbc);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            // Wait for all 3 frames
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? value = null;
            while (sw.ElapsedMilliseconds < 5000)
            {
                value = ctx.GetSignalValue("TestMsg.TestSignal");
                if (value == 0x30) break; // Last frame value
                await Task.Delay(50);
            }

            value.Should().Be(0x30, "signal cache should reflect latest frame value");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }
}

/// <summary>Fake fixture resolver for integration tests.</summary>
internal sealed class FakeFixtureResolver : IFixtureResolver
{
    public ITestFixture Resolve(string key) => new NoOpFixture();
}

internal sealed class NoOpFixture : ITestFixture
{
    public Task SetupAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
}
