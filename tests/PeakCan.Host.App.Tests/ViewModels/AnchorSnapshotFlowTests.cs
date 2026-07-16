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
        var frameSource = Substitute.For<IFrameSourceProvider>();
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sessionLib = new TraceSessionLibrary(NullLogger<TraceSessionLibrary>.Instance);
        var logger = NullLogger<TraceViewerViewModel>.Instance;
        frameSource.GetFrames(Arg.Any<string>()).Returns(Array.Empty<ReplayFrame>());
        // v3.52.0 MINOR T9: ctor extended with 5 required analysis params.
        // AnchorSnapshotFlow tests don't exercise the analysis pipeline,
        // so passing fresh Core-side stubs is sufficient.
        var vm = new TraceViewerViewModel(
            registry, dbc, logger, sessionLib,
            new EvidenceExtractor(),
            new LocalAnalyzer(),
            new AnalysisSessionRegistry(),
            new NotImplementedLlmProvider(),
            frameSource);
        if (greenSet) vm.RefreshAtAnchor(1.0);
        if (blueSet) vm.RefreshAtAnchorBlue(1.5);
        return vm;
    }
}
