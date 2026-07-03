using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceViewerViewModelTests
{
    private static ITraceViewerService MakeFakeService() => Substitute.For<ITraceViewerService>();

    private static ILogger<TraceViewerViewModel> MakeFakeLogger()
        => Substitute.For<ILogger<TraceViewerViewModel>>();

    // TBD-2: substitute the concrete DbcService via NSubstitute's
    // constructor pattern. The production ctor accepts DbcService
    // directly (not an interface) — partial + virtual methods let
    // NSubstitute intercept LoadAsync without touching the disk.
    private static DbcService MakeFakeDbcService()
        => Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());

    [Fact]
    public void Ctor_Empty_NoSignalsNoCharts()
    {
        var sut = new TraceViewerViewModel(MakeFakeService(), MakeFakeDbcService(), MakeFakeLogger());
        sut.Signals.Should().BeEmpty();
        sut.ChartViewModel.Series.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenFileAsync_InvokesServiceLoadAsync()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        await sut.OpenFileAsync("C:/fake.asc");
        await svc.Received(1).LoadAsync("C:/fake.asc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PlayCommand_InvokesServicePlay()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        sut.PlayCommand.Execute(null);
        svc.Received(1).Play();
    }

    [Fact]
    public void PauseCommand_InvokesServicePause()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        sut.PauseCommand.Execute(null);
        svc.Received(1).Pause();
    }

    [Fact]
    public void StopCommand_InvokesServiceStop()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        sut.StopCommand.Execute(null);
        svc.Received(1).Stop();
    }
}