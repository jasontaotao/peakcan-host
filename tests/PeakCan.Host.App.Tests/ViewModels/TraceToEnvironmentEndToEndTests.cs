using System.IO;
using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceToEnvironmentEndToEndTests : IDisposable
{
    private readonly string _tracePath = Path.Combine(
        Path.GetTempPath(), $"pch-m5-e2e-{Guid.NewGuid():N}.asc");
    private readonly string _suitePath = Path.Combine(
        Path.GetTempPath(), $"pch-m5-e2e-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task TraceToSuite_Imports_Periodic_Groups_Rejects_Conflict_And_Preserves_File()
    {
        CreateTrace();
        CreateSuite();
        var vm = new TraceToEnvironmentViewModel(null, new SuiteEnvironmentWriter())
        {
            TracePath = _tracePath,
            SuitePath = _suitePath,
        };

        await vm.LoadAsync();

        vm.BlockingErrors.Should().BeEmpty();
        vm.Candidates.Should().HaveCount(4);

        var regular = vm.Candidates.Single(row => row.NodeName == "Trace-ID-0x123" && row.Channel == "A");
        var mirrored = vm.Candidates.Single(row => row.NodeName == "Trace-ID-0x123" && row.Channel == "B");
        var j1939 = vm.Candidates.Single(row => row.Identity.StartsWith("J1939", StringComparison.Ordinal));
        var irregular = vm.Candidates.Single(row => row.Include == false);

        regular.NodeName.Should().Be("Trace-ID-0x123");
        regular.IntervalMs.Should().Be(20);
        regular.Include.Should().BeTrue();
        j1939.NodeName.Should().Be("Trace-SA-0x55");
        j1939.IntervalMs.Should().Be(50);
        j1939.Include.Should().BeTrue();
        irregular.Include.Should().BeFalse();
        mirrored.Channel.Should().Be("B");

        mirrored.NodeName = "Trace-ID-0x123-B";

        await vm.WriteSuiteAsync();

        vm.BlockingErrors.Should().BeEmpty();
        vm.Status.Should().Contain("写入成功");

        var suite = ReloadSuite();
        suite.Environment.Should().HaveCount(3);
        var importedRegular = suite.Environment!.Single(n => n.Name == "Trace-ID-0x123");
        var importedJ1939 = suite.Environment!.Single(n => n.Name == "Trace-SA-0x55");

        importedRegular.Channel.Should().Be("A");
        importedRegular.SourceChannel.Should().Be("A");
        importedRegular.Identity.Should().BeOfType<RawCanNodeIdentity>();
        importedRegular.Messages.Should().ContainSingle();
        importedRegular.Messages[0].Ref.Should().Be(new CanMessageRef(0x123, false));
        importedRegular.Messages[0].IntervalMs.Should().Be(20);

        importedJ1939.Channel.Should().Be("A");
        importedJ1939.SourceChannel.Should().Be("A");
        importedJ1939.Identity.Should().BeOfType<J1939NodeIdentity>();
        importedJ1939.Messages.Should().ContainSingle();
        var jRef = Assert.IsType<J1939MessageRef>(importedJ1939.Messages[0].Ref);
        jRef.Pgn.Should().Be(0xff00);
        jRef.Sa.Should().Be(0x55);

        var afterFirstWrite = await File.ReadAllTextAsync(_suitePath);
        mirrored.NodeName = "Trace-ID-0x123-B";

        await vm.WriteSuiteAsync();

        vm.BlockingErrors.Should().Contain(e => e.Contains("Trace-ID-0x123"));
        vm.Status.Should().Contain("写入失败");
        File.ReadAllText(_suitePath).Should().Be(afterFirstWrite);
    }

    private void CreateTrace()
    {
        var lines = new[]
        {
            "date Wed Sep 4 10:00:00.000 2026",
            "base hex  timestamps absolute",
            "no internal events logged",
            " 0.000000 01  123  2  01 02",
            " 0.020000 01  123  2  01 02",
            " 0.040000 01  123  2  01 02",
            " 0.060000 01  123  2  01 02",
            " 0.080000 01  123  2  01 02",
            " 0.000000 02  123  2  11 12",
            " 0.020000 02  123  2  11 12",
            " 0.040000 02  123  2  11 12",
            " 0.060000 02  123  2  11 12",
            " 0.080000 02  123  2  11 12",
            " 0.000000 01  456  2  03 04",
            " 0.011000 01  456  2  03 04",
            " 0.173000 01  456  2  03 04",
            " 0.181000 01  456  2  03 04",
            " 0.679000 01  456  2  03 04",
            " 0.000000 01  18FF0055x  2  05 06",
            " 0.050000 01  18FF0055x  2  05 06",
            " 0.100000 01  18FF0055x  2  05 06",
            " 0.150000 01  18FF0055x  2  05 06",
            " 0.200000 01  18FF0055x  2  05 06",
        };
        File.WriteAllLines(_tracePath, lines);
    }

    private void CreateSuite()
    {
        var channels = new[]
        {
            new ChannelConfig("A", "01", null, false),
            new ChannelConfig("B", "02", null, false),
        };
        var suite = new TestSuite(
            "M5 E2E", [], [], [], new TestSuiteConfig(), Channels: channels);
        File.WriteAllText(_suitePath, JsonSerializer.Serialize(suite, HILJsonOptions.Default));
    }

    private TestSuite ReloadSuite()
        => JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(_suitePath), HILJsonOptions.Default)!;

    public void Dispose()
    {
        File.Delete(_tracePath);
        File.Delete(_suitePath);
        GC.SuppressFinalize(this);
    }
}





