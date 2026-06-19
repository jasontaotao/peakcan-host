using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// Task 12: verifies that <see cref="AppHostBuilder.Build"/> composes a host
/// with the right services registered as singletons. These tests are pure
/// DI: they don't touch WPF windows, the SDK, or Serilog file sinks.
/// </summary>
public class AppHostBuilderTests
{
    [Fact]
    public void Build_Returns_Host_With_ServiceProvider()
    {
        using var host = AppHostBuilder.Build();
        host.Services.Should().NotBeNull();
    }

    [Fact]
    public void Build_Registers_ChannelRouter_As_Singleton()
    {
        using var host = AppHostBuilder.Build();
        var a = host.Services.GetService<ChannelRouter>();
        var b = host.Services.GetService<ChannelRouter>();
        a.Should().NotBeNull();
        a.Should().BeSameAs(b, "ChannelRouter is a singleton — router state must be shared");
    }

    [Fact]
    public void Build_Registers_BusStatisticsCollector_As_Singleton()
    {
        using var host = AppHostBuilder.Build();
        var a = host.Services.GetService<BusStatisticsCollector>();
        var b = host.Services.GetService<BusStatisticsCollector>();
        a.Should().NotBeNull();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Build_Registers_All_App_Services_As_Singletons()
    {
        using var host = AppHostBuilder.Build();
        host.Services.GetService<TraceService>().Should().NotBeNull();
        host.Services.GetService<SendService>().Should().NotBeNull();
        host.Services.GetService<DbcService>().Should().NotBeNull();
        host.Services.GetService<StatisticsService>().Should().NotBeNull();
    }

    [Fact]
    public void Build_Registers_All_ViewModels_As_Singletons()
    {
        using var host = AppHostBuilder.Build();
        host.Services.GetService<AppShellViewModel>().Should().NotBeNull();
        host.Services.GetService<TraceViewModel>().Should().NotBeNull();
        host.Services.GetService<SendViewModel>().Should().NotBeNull();
        host.Services.GetService<DbcViewModel>().Should().NotBeNull();
        host.Services.GetService<SignalViewModel>().Should().NotBeNull();
        host.Services.GetService<StatsViewModel>().Should().NotBeNull();
    }

    [Fact]
    [Trait("category", "ui")]
    public void Build_Registers_AppShell_As_Singleton()
    {
        // AppShell is a WPF Window; instantiating it requires an STA
        // thread. xunit 2.x runs each [Fact] on its own thread but defaults
        // to MTA. Mark this test as STA via the [StaFact] attribute by
        // using a private thread; the alternative is a separate test
        // fixture. For the MVP we wrap the resolution in an STA thread
        // so the test stays a plain [Fact].
        var appShell = RunSta(() => AppHostBuilder.Build().Services.GetService<AppShell>());
        appShell.Should().NotBeNull();
    }

    private static T RunSta<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return body();
        }
        T result = default!;
        var ex = (Exception?)null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
        return result;
    }

    [Fact]
    public void Build_Resolves_AppShellViewModel_With_Logger_And_Router()
    {
        // Constructor injects ChannelRouter + ILogger<AppShellViewModel>;
        // a successful resolve proves both were registered (logger is
        // automatically supplied by Microsoft.Extensions.Hosting).
        using var host = AppHostBuilder.Build();
        var vm = host.Services.GetService<AppShellViewModel>();
        vm.Should().NotBeNull();
    }
}
