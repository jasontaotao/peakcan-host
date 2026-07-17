using FluentAssertions;
using Microsoft.Extensions.Logging;
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

public class AnchorSnapshotFlowTests
{
    [Fact]
    public void CanLockAnchor_GreenOnly_ReturnsFalse()
    {
        var vm = MakeVm(greenSet: true, blueSet: false);
        vm.LockAnchorCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanLockAnchor_BothAnchors_ReturnsTrue()
    {
        var vm = MakeVm(greenSet: true, blueSet: true);
        vm.LockAnchorCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LockAnchor_BothSet_CreatesSnapshot()
    {
        var vm = MakeVm(greenSet: true, blueSet: true);
        vm.LockAnchorCommand.Execute(null);
        vm.CurrentAnchorSnapshot.Should().NotBeNull();
    }

    [Fact]
    public void LockAnchor_BlueMissing_SetsErrorMessageAndDoesNotCreate()
    {
        var vm = MakeVm(greenSet: true, blueSet: false);
        vm.LockAnchorCommand.Execute(null);
        vm.CurrentAnchorSnapshot.Should().BeNull();
        vm.ErrorMessage.Should().Contain("比较锚");
    }

    private static TraceViewerViewModel MakeVm(bool greenSet, bool blueSet)
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        var dbc = Substitute.For<DbcService>(Substitute.For<Microsoft.Extensions.Logging.ILogger<DbcService>>());
        var sessionLib = new TraceSessionLibrary(NullLogger<TraceSessionLibrary>.Instance);
        var logger = NullLogger<TraceViewerViewModel>.Instance;

        // v3.52.1 PATCH T3: explicit Substitute.For<> for all 5 analysis
        // deps (sister pattern to AnalysisFlowTests). The 3 Core-side
        // classes were unsealed to make them proxyable. AnchorSnapshotFlow
        // tests do not exercise the analysis pipeline, so any proxy is fine.
        var vm = new TraceViewerViewModel(
            registry, dbc, logger, sessionLib,
            Substitute.For<EvidenceExtractor>(),
            Substitute.For<LocalAnalyzer>(),
            Substitute.For<AnalysisSessionRegistry>(),
            Substitute.For<ILlmProvider>(),
            Substitute.For<IFrameSourceProvider>(),
            apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());
        if (greenSet) vm.RefreshAtAnchor(1.0);
        if (blueSet) vm.RefreshAtAnchorBlue(1.5);
        return vm;
    }
}
