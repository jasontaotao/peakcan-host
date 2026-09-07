using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using System.IO;
using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public sealed class HilViewModelRunConfigurationTests
{
    [Fact]
    public void CanRun_IsFalse_WhenNoCasesSelected()
    {
        var (vm, path) = CreateConfiguredHardwareViewModel(cases: ["A"]);
        vm.ReloadSuiteCommand.Execute(null);
        vm.SelectNoCasesCommand.Execute(null);
        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void CanRun_IsFalse_WhenDeclaredChannelsExceedConnected()
    {
        var (_, path) = CreateConfiguredHardwareViewModel(cases: ["A"], channelCount: 2);
        var source = new ConnectedChannelsSource();
        source.Publish([new HilViewModel.ConnectedChannel(0x51, BaudRate.CanFd1Mbps, true, "USB1", Substitute.For<ICanChannel>())]);
        var vm = NewVm(source);
        Configure(vm, path);

        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void CanRun_IsFalse_WhenChannelDeclarationsDuplicate()
    {
        var (_, path) = CreateConfiguredHardwareViewModel(cases: ["A"], channels: ["bus-a", "bus-a"]);
        var vm = NewVm();
        Configure(vm, path);

        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedCaseNames_IsNull_WhenNoCaseListExists()
    {
        var (vm, _) = CreateConfiguredHardwareViewModel(cases: []);
        var request = vm.BuildRunRequest(null);
        Assert.Null(request.SelectedCaseNames);
    }
    [Fact]
    public void CaseFilter_AllSelect_AffectsOnlyVisibleItems()
    {
        var (vm, _) = CreateConfiguredHardwareViewModel(cases: ["Alpha", "Beta"]);
        vm.ReloadSuiteCommand.Execute(null);
        vm.SelectNoCasesCommand.Execute(null);
        vm.CaseFilter = "Alpha";
        vm.SelectAllCasesCommand.Execute(null);

        Assert.True(vm.AvailableCases.First(c => c.Name == "Alpha").IsSelected);
        Assert.False(vm.AvailableCases.First(c => c.Name == "Beta").IsSelected);
    }

    [Fact]
    public async Task CaseLogDirectory_IsPassedToRequest()
    {
        var runner = Substitute.For<IHilRunnerService>();
        var (vm, _) = CreateConfiguredHardwareViewModel(cases: ["A"]);
        vm.CaseLogDirectory = @"C:\logs\hil";

        var request = vm.BuildRunRequest(null);

        Assert.Equal(@"C:\logs\hil", request.CaseLogDirectory);
    }

    [Fact]
    public void QueuePreflight_ClearsStaleCriticalWarningImmediately()
    {
        var vm = NewVm();
        vm.PreflightWarning = "old critical";
        vm.DbcPath = @"C:\changed.dbc";

        Assert.Equal("", vm.PreflightWarning);
    }

    [Fact]
    public void SelectNoCases_AffectsOnlyVisibleItems()
    {
        var (vm, _) = CreateConfiguredHardwareViewModel(cases: ["Alpha", "Beta"]);
        vm.ReloadSuiteCommand.Execute(null);
        vm.CaseFilter = "Alpha";
        vm.SelectNoCasesCommand.Execute(null);

        Assert.False(vm.AvailableCases.First(c => c.Name == "Alpha").IsSelected);
        Assert.True(vm.AvailableCases.First(c => c.Name == "Beta").IsSelected);
    }

    private static (HilViewModel Vm, string Path) CreateConfiguredHardwareViewModel(
        IReadOnlyList<string> cases,
        IReadOnlyList<string>? channels = null,
        int channelCount = 0,
        IHilRunnerService? runner = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"run-config-{Guid.NewGuid():N}.suite.json");
        var channelJson = string.Join(",", Enumerable.Range(0, Math.Max(channels?.Count ?? channelCount, 0))
            .Select(i => $"{{\"name\":\"{channels?[i] ?? $"bus-{i}"}\"}}"));
        var caseJson = string.Join(",", cases.Select(n => $"{{\"id\":\"{n}\",\"name\":\"{n}\"}}"));
        File.WriteAllText(path, $$"""
        {"name":"S","cases":[{{caseJson}}],"globalCaseFixtureKeys":[],"suiteFixtureKeys":[],"config":{},"channels":[{{channelJson}}]}
        """);
        var vm = NewVm();
        Configure(vm, path);
        return (vm, path);
    }

    private static HilViewModel NewVm(IConnectedChannelsSource? source = null, IHilRunnerService? runner = null) => new(
        runner ?? Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance,
        Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(),
        Substitute.For<IHilReportService>(), connectedChannelsSource: source);

    private static void Configure(HilViewModel vm, string suitePath)
    {
        vm.SuitePath = suitePath;
        vm.DbcPath = @"C:\dbc.dbc";
        vm.SelectedMode = HilMode.Hardware;
        vm.RefreshAvailableChannels();
        vm.ReloadSuiteCommand.Execute(null);
        vm.RefreshAvailableChannels();
        vm.HardwareChannel = "USB1";
    }
}
