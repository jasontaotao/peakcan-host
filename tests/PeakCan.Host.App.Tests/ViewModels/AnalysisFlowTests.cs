using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using PeakCan.Host.App.Services.AnalysisApiKey;

namespace PeakCan.Host.App.Tests.ViewModels;

public class AnalysisFlowTests
{
    [Fact]
    public async Task RunAnalysisAsync_NoAnchorSnapshot_SetsErrorMessage()
    {
        var vm = MakeVm();
        await vm.RunAnalysisAsync();
        vm.CurrentAnalysisSession.Should().BeNull();
        vm.ErrorMessage.Should().Contain("锁定");
    }

    [Fact]
    public async Task RunAnalysisAsync_WithAnchor_CreatesSession()
    {
        var vm = MakeVm();
        vm.RefreshAtAnchor(1.0);
        vm.RefreshAtAnchorBlue(1.5);
        vm.LockAnchorCommand.Execute(null);
        vm.CurrentAnchorSnapshot.Should().NotBeNull();

        await vm.RunAnalysisAsync();
        vm.CurrentAnalysisSession.Should().NotBeNull();
    }

    private static TraceViewerViewModel MakeVm()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        var frameSource = Substitute.For<IFrameSourceProvider>();
        var dbc = Substitute.For<DbcService>(Substitute.For<Microsoft.Extensions.Logging.ILogger<DbcService>>());
        var sessionLib = new TraceSessionLibrary(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"analysis-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var logger = NullLogger<TraceViewerViewModel>.Instance;

        // v3.52.1 PATCH T3: mock the 5 analysis deps via Substitute.For<>
        // (sister pattern to DbcService above). The 3 Core-side classes
        // were unsealed to make them proxyable (Castle.DynamicProxy cannot
        // proxy sealed types). frameSource.GetFrames stub removed — the
        // mocked IFrameSourceProvider returns default empty by default.
        return new TraceViewerViewModel(
            registry, dbc, logger, sessionLib,
            Substitute.For<EvidenceExtractor>(),
            Substitute.For<LocalAnalyzer>(),
            Substitute.For<AnalysisSessionRegistry>(),
            Substitute.For<ILlmProvider>(),
            frameSource,
            apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
                Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
                Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));
    }

    [Fact]
    public void RunAnalysisCommand_CanExecute_RefreshedAfterLockAnchor()
    {
        // v3.52.1 PATCH T2+T3: LockAnchor must explicitly notify RunAnalysisCommand
        // (Minor 3 from v3.52.0 review). Without this trigger, the UI Run button
        // stays disabled even after the user has created a valid snapshot.
        var vm = MakeVm();
        Assert.False(vm.RunAnalysisCommand.CanExecute(null),
            "before LockAnchor, no snapshot -> CanExecute=false");

        vm.RefreshAtAnchor(1.0);
        vm.RefreshAtAnchorBlue(1.5);
        vm.LockAnchorCommand.Execute(null);

        Assert.True(vm.RunAnalysisCommand.CanExecute(null),
            "after LockAnchor creates snapshot, CanExecute=true");
    }
}
