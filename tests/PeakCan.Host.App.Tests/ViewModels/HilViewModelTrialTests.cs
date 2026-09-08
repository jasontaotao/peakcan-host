using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using System.IO;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public sealed class HilViewModelTrialTests
{
    [Fact]
    public async Task Trial_PreviewResult_IsNotPresentedAsFullHandshake()
    {
        var source = new ConnectedChannelsSource();
        source.Publish([new HilViewModel.ConnectedChannel(0x51, BaudRate.CanFd1Mbps, true, "USB1", Substitute.For<ICanChannel>())]);
        var trialService = Substitute.For<ITrialRunService>();
        trialService.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<TrialChannelContext>>(), Arg.Any<CancellationToken>())
            .Returns(new TrialRunResult(true,
                [new TrialDiagnostic("CRM", true, "Frame-stream preview: CRM → BRM (50ms)", [])],
                IsFullHandshakeCheck: false));

        var vm = new HilViewModel(
            Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance,
            Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(),
            Substitute.For<IHilReportService>(),
            connectedChannelsSource: source,
            trialRunService: trialService);
        var suitePath = Path.Combine(Path.GetTempPath(), $"trial-{Guid.NewGuid():N}.suite.json");
        await File.WriteAllTextAsync(suitePath, """{"name":"S","cases":[],"globalCaseFixtureKeys":[],"suiteFixtureKeys":[],"config":{}}""");
        vm.SuitePath = suitePath;
        vm.SuitePath = suitePath;
        vm.RefreshAvailableChannels();

        Assert.NotEmpty(vm.AvailableChannels);
        vm.TrialRunEnvironmentCommand.Execute(null);
        await vm.TrialRunEnvironmentCommand.ExecutionTask!;
        Assert.Contains("preview（无法完整判定）", vm.TrialRunStatus);
        Assert.Single(vm.TrialDiagnostics);
    }

    [Fact]
    public void CanTrial_RequiresHardwareModeAndConnectedChannel()
    {
        var vm = new HilViewModel(
            Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance,
            Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(),
            Substitute.For<IHilReportService>());
        vm.SuitePath = @"C:\suite.json";
        Assert.False(vm.TrialRunEnvironmentCommand.CanExecute(null));
        vm.SelectedMode = HilMode.Hardware;
        Assert.False(vm.TrialRunEnvironmentCommand.CanExecute(null));
    }
}
