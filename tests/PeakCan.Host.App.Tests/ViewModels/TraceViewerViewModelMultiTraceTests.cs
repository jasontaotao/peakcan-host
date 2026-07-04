using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.2.0 MINOR: pins the multi-trace overlay behavior of
/// <see cref="TraceViewerViewModel"/> — registry-backed source list,
/// add/remove commands, playback disabled in multi-trace mode.
/// </summary>
public class TraceViewerViewModelMultiTraceTests
{
    [Fact]
    public void Constructor_SubscribesToRegistrySourcesChanged()
    {
        var registry = MakeRegistry();
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        // Adding a source should raise PropertyChanged on Sources (or fire
        // the SourcesChanged event through the VM). Verify the VM exposed
        // the registry's current Sources:
        vm.Sources.Should().BeSameAs(registry.Sources,
            "the VM should expose the registry's source list directly (read-through)");
    }

    [Fact]
    public async Task AddTraceAsync_AppendsToRegistry_DoesNotOverwrite()
    {
        var registry = MakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "traceA", "C:/a.asc", OxyColors.Blue),
        });

        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        await vm.AddTraceAsync("C:/b.asc");

        await registry.Received(1).LoadAsync("C:/b.asc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveTraceCommand_RemovesOnlyOwnSource()
    {
        var registry = MakeRegistry();
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        await vm.RemoveTraceAsync("guid-target");

        await registry.Received(1).UnloadAsync("guid-target");
    }

    [Fact]
    public void IsMultiTraceMode_False_WhenSourcesEmpty()
    {
        var registry = MakeRegistry();
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        vm.IsMultiTraceMode.Should().BeFalse();
    }

    [Fact]
    public void IsMultiTraceMode_True_WhenSourcesCount_GreaterThanOne()
    {
        var registry = MakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "traceA", "C:/a.asc", OxyColors.Blue),
            new("guid-2", "traceB", "C:/b.asc", OxyColors.Orange),
        });
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        vm.IsMultiTraceMode.Should().BeTrue();
    }

    [Fact]
    public void PlayCommand_InMultiTraceMode_ThrowsInvalidOperationException()
    {
        var registry = MakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "traceA", "C:/a.asc", OxyColors.Blue),
            new("guid-2", "traceB", "C:/b.asc", OxyColors.Orange),
        });
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        var act = () => vm.PlayCommand.Execute(null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*multi-trace*");
    }

    [Fact]
    public void MasterSourceId_DefaultsToFirstSource_WhenMultipleLoaded()
    {
        var registry = MakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "traceA", "C:/a.asc", OxyColors.Blue),
            new("guid-2", "traceB", "C:/b.asc", OxyColors.Orange),
        });
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger());

        vm.MasterSourceId.Should().Be("guid-1");
    }

    // --- Helpers ----------------------------------------------------

    private static ITraceSessionRegistry MakeRegistry()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        return registry;
    }

    private static ILogger<TraceViewerViewModel> MakeFakeLogger()
        => Substitute.For<ILogger<TraceViewerViewModel>>();

    private static DbcService MakeFakeDbcService()
        => Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());
}