using System.IO;
using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceToEnvironmentViewModelTests : IDisposable
{
    private readonly string _tracePath = Path.Combine(
        Path.GetTempPath(), $"pch-m5-{Guid.NewGuid():N}.asc");
    private readonly string _suitePath = Path.Combine(
        Path.GetTempPath(), $"pch-m5-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Load_Analyzes_Trace_And_Write_Appends_Selected_Node()
    {
        const string asc = @"
 0.000000 51  123  2  01 02
 0.020000 51  123  2  01 02
 0.040000 51  123  2  01 02
 0.060000 51  123  2  01 02
 0.080000 51  123  2  01 02
";
        await File.WriteAllTextAsync(_tracePath, asc);
        var suite = new TestSuite(
            "M5", [], [], [], new TestSuiteConfig(), Channels: [new ChannelConfig("A", "51", null, false)]);
        await File.WriteAllTextAsync(_suitePath, JsonSerializer.Serialize(suite, HILJsonOptions.Default));

        var vm = new TraceToEnvironmentViewModel(null, new SuiteEnvironmentWriter())
        {
            TracePath = _tracePath,
            SuitePath = _suitePath,
        };
        await vm.LoadAsync();

        vm.BlockingErrors.Should().BeEmpty();
        vm.Candidates.Should().ContainSingle();
        vm.Candidates[0].Channel.Should().Be("A");
        vm.Candidates[0].Include.Should().BeTrue();

        await vm.WriteSuiteAsync();

        vm.BlockingErrors.Should().BeEmpty();
        vm.Status.Should().Contain("写入成功");
        var reloaded = JsonSerializer.Deserialize<TestSuite>(await File.ReadAllTextAsync(_suitePath), HILJsonOptions.Default);
        reloaded!.Environment.Should().ContainSingle(n => n.Name == "Trace-ID-0x123");
        reloaded.Environment![0].Channel.Should().Be("A");
        reloaded.Environment![0].SourceChannel.Should().Be("A");
    }

    public void Dispose()
    {
        File.Delete(_tracePath);
        File.Delete(_suitePath);
        GC.SuppressFinalize(this);
    }
}
