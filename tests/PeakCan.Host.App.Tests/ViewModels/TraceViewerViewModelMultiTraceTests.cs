using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;
using PeakCan.Host.App.Services.AnalysisApiKey;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.2.0 MINOR: pins the multi-trace overlay behavior of
/// <see cref="TraceViewerViewModel"/> — registry-backed source list,
/// add/remove commands, playback disabled in multi-trace mode.
/// </summary>
public class TraceViewerViewModelMultiTraceTests
{
    // v3.5.0 MINOR: real TraceSessionLibrary against a per-test temp
    // path. Tests in this file do not assert on bundle round-trip; the
    // library is wired so the VM ctor is satisfied.
    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

    [Fact]
    public void Constructor_SubscribesToRegistrySourcesChanged()
    {
        var registry = MakeRegistry();
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());

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
        // v3.11.4 PATCH: AddTraceAsync is parameterless now; the file dialog
        // is wired via IFileDialogService. Stub the dialog to return the path
        // the test wants the registry to receive.
        var dialog = Substitute.For<PeakCan.Host.Core.IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/b.asc");
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog, apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());

        await vm.AddTraceAsync();

        await registry.Received(1).LoadAsync("C:/b.asc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveTraceCommand_RemovesOnlyOwnSource()
    {
        var registry = MakeRegistry();
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());

        await vm.RemoveTraceAsync("guid-target");

        await registry.Received(1).UnloadAsync("guid-target");
    }

    [Fact]
    public void PlayCommand_InMultiTraceMode_DoesNotThrow_DrivesAllServices()
    {
        // v3.3.0 MINOR: sync playback now allowed in multi-trace mode
        var registry = MakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "traceA", "C:/a.asc", OxyColors.Blue),
            new("guid-2", "traceB", "C:/b.asc", OxyColors.Orange),
        });
        var dbcService = MakeFakeDbcService();
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());

        // Pre-load the per-source services onto the fake registry (in production
        // the real registry hands them out via LoadAsync). The VM's
        // SourcesChanged handler reads them via GetService when iterating.
        var svcA = Substitute.For<ITraceViewerService>();
        var svcB = Substitute.For<ITraceViewerService>();
        registry.GetService("guid-1").Returns(svcA);
        registry.GetService("guid-2").Returns(svcB);
        // Fire SourcesChanged so the VM rebuilds _allServices for this fixture.
        registry.SourcesChanged += Raise.Event<Action>();

        var act = () => vm.PlayCommand.Execute(null);

        act.Should().NotThrow();
        svcA.Received(1).Play();
        svcB.Received(1).Play();
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
        var vm = new TraceViewerViewModel(registry, dbcService, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());

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