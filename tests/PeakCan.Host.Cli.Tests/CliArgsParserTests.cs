using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.Cli;
using Xunit;

namespace PeakCan.Host.Cli.Tests;

public class CliArgsParserTests
{
    private static readonly string[] _baseArgs = { "--dbc", "x.dbc", "--suite", "y.json" };

    private static string[] With(params string[] extra) => _baseArgs.Concat(extra).ToArray();

    [Fact]
    public void Parse_HwOnly_NoTrace_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--hw", "USB1"));
        Assert.Equal("USB1", cli.HardwareChannel);
        Assert.Null(cli.TracePath);
    }

    [Fact]
    public void Parse_TraceOnly_NoHw_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--trace", "x.asc"));
        Assert.Equal("x.asc", cli.TracePath);
        Assert.Null(cli.HardwareChannel);
    }

    [Fact]
    public void Parse_BothHwAndTrace_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CliArgsParser.Parse(With("--hw", "USB1", "--trace", "x.asc")));
        Assert.Contains("Cannot use --trace and --hw", ex.Message);
    }

    [Fact]
    public void Parse_NeitherHwNorTraceNorEcu_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => CliArgsParser.Parse(_baseArgs));
        Assert.Contains("Must specify --trace, --hw, --ecu, or --matrix", ex.Message);
    }

    [Fact]
    public void Parse_UdsReqHex_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--hw", "USB1", "--uds-req", "0x7DF"));
        Assert.Equal(0x7DFu, cli.UdsRequestId);
    }

    [Fact]
    public void Parse_UdsReqDecimal_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--hw", "USB1", "--uds-req", "2015"));
        Assert.Equal(0x7DFu, cli.UdsRequestId); // 2015 decimal = 0x7DF hex
    }

    // --- Phase 3: --ecu flag tests ---

    [Fact]
    public void Parse_EcuFlag_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--ecu", "bms_sim.json"));
        Assert.Equal("bms_sim.json", cli.EcuScriptPath);
        Assert.Null(cli.HardwareChannel);
        Assert.Null(cli.TracePath);
    }

    [Fact]
    public void Parse_EcuAndHw_AreMutuallyExclusive()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CliArgsParser.Parse(With("--ecu", "bms_sim.json", "--hw", "USB1")));
        Assert.Contains("Cannot use --ecu and --hw", ex.Message);
    }

    [Fact]
    public void Parse_EcuDefaultsNull()
    {
        var cli = CliArgsParser.Parse(With("--hw", "USB1"));
        Assert.Null(cli.EcuScriptPath);
    }

    // --- Phase 3 Sprint 6: --matrix flag tests ---

    [Fact]
    public void Parse_MatrixFlag_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--matrix", "powertrain.json"));
        Assert.Equal("powertrain.json", cli.MatrixPath);
        Assert.Null(cli.EcuScriptPath);
        Assert.Null(cli.HardwareChannel);
    }

    [Fact]
    public void Parse_MatrixAndEcu_AreMutuallyExclusive()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CliArgsParser.Parse(With("--matrix", "powertrain.json", "--ecu", "bms_sim.json")));
        Assert.Contains("Cannot use --matrix and --ecu", ex.Message);
    }

    // --- Phase 7 Unit D: --gateway flag tests ---

    [Fact]
    public void Parse_GatewayPath_Succeeds()
    {
        var cli = CliArgsParser.Parse(With("--hw", "USB1", "--gateway", "gateway.json"));
        Assert.Equal("gateway.json", cli.GatewayPath);
    }

    [Fact]
    public void Parse_NoGateway_DefaultsNull()
    {
        var cli = CliArgsParser.Parse(With("--hw", "USB1"));
        Assert.Null(cli.GatewayPath);
    }
}
