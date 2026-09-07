using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.App.Services.HilPreflight;
using System.IO;
using Xunit;

namespace PeakCan.Host.App.Tests.ServicesSuitePreflight;

public sealed class SuitePreflightServiceTests
{
    private readonly SuitePreflightService _service = new(NullLogger<SuitePreflightService>.Instance);

    [Fact]
    public async Task BadJson_ReturnsCriticalWithLineNumber()
    {
        var suite = WriteFile(".suite.json", "{ not json");

        var result = await _service.RunAsync(Request(suite));

        Assert.True(result.HasCritical);
        Assert.Contains(result.Issues, i => i.Severity == PreflightSeverity.Critical && i.Message.Contains('行'));
    }

    [Fact]
    public async Task MissingSuiteOrDbc_ReturnsCritical()
    {
        var result = await _service.RunAsync(new HilPreflightRequest(
            @"C:\missing.suite.json", @"C:\missing.dbc", null, null, null, null, HilMode.TraceReplay));

        Assert.True(result.HasCritical);
        Assert.Contains(result.Issues, i => i.Message.Contains("Suite"));
        Assert.Contains(result.Issues, i => i.Message.Contains("DBC"));
    }

    [Fact]
    public async Task ModeSpecificPath_Missing_ReturnsCritical()
    {
        var suite = WriteValidSuite();
        var result = await _service.RunAsync(new HilPreflightRequest(
            suite, WriteFile(".dbc", "Version \"\""), null, null, null, null, HilMode.TraceReplay));

        Assert.True(result.HasCritical);
        Assert.Contains(result.Issues, i => i.Message.Contains("Trace"));
    }

    [Fact]
    public async Task DanglingEnvironmentChannel_ReturnsCritical()
    {
        var suite = WriteValidSuite("""
        "environment":[{"name":"T","identity":{"kind":"rawCan"},"channel":"bus-a"}]
        """);
        var result = await _service.RunAsync(Request(suite));

        Assert.True(result.HasCritical);
        Assert.Contains(result.Issues, i => i.Message.Contains("bus-a"));
    }

    [Fact]
    public async Task DuplicateOrInvalidChannelDeclaration_ReturnsCritical()
    {
        var suite = WriteValidSuite("""
        "channels":[{"name":"bus-a"},{"name":"bus-a"}]
        """);
        var result = await _service.RunAsync(Request(suite));

        Assert.True(result.HasCritical);
        Assert.Contains(result.Issues, i => i.Message.Contains("重复"));
    }

    [Fact]
    public async Task ValidSuite_ReturnsNoCritical()
    {
        var suite = WriteValidSuite();
        var result = await _service.RunAsync(Request(suite));

        Assert.False(result.HasCritical);
    }

    private HilPreflightRequest Request(string suite) => new(
        suite,
        WriteFile(".dbc", "Version \"\""),
        WriteFile(".asc", ""),
        WriteFile(".ecu.json", "{}"),
        WriteFile(".matrix.json", "{}"),
        null,
        HilMode.VirtualEcu);

    private string WriteValidSuite(string extra = "") => WriteFile(".suite.json", $$"""
    {
      "name":"S",
      "cases":[],
      "globalCaseFixtureKeys":[],
      "suiteFixtureKeys":[],
      "config":{}{{(extra.Length == 0 ? "" : ",")}}{{extra}}
    }
    """);

    private static string WriteFile(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }
}
