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
        frameSource.GetFrames(Arg.Any<string>()).Returns(Array.Empty<ReplayFrame>());

        var vm = new TraceViewerViewModel(registry, dbc, logger, sessionLib);
        var fields = new[]
        {
            ("_evidenceExtractor", (object)new EvidenceExtractor()),
            ("_localAnalyzer", (object)new LocalAnalyzer()),
            ("_sessionRegistry", (object)new AnalysisSessionRegistry()),
            ("_llmProvider", (object)new NotImplementedLlmProvider()),
            ("_frameSource", (object)frameSource),
        };
        foreach (var (name, value) in fields)
        {
            var field = typeof(TraceViewerViewModel).GetField(
                name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.Should().NotBeNull($"field {name} must exist on TraceViewerViewModel");
            field!.SetValue(vm, value);
        }

        return vm;
    }
}
