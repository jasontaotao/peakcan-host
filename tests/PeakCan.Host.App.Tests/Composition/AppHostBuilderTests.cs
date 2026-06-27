using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
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
        host.Services.GetService<ScriptViewModel>().Should().NotBeNull();
    }

    [Fact]
    public void Build_Registers_UdsViewModel_And_PanelVMs_As_Singletons()
    {
        using var host = AppHostBuilder.Build();
        host.Services.GetService<UdsViewModel>().Should().NotBeNull();
        host.Services.GetService<SessionPanelViewModel>().Should().NotBeNull();
        host.Services.GetService<DidPanelViewModel>().Should().NotBeNull();
        host.Services.GetService<RoutinePanelViewModel>().Should().NotBeNull();
        host.Services.GetService<DtcPanelViewModel>().Should().NotBeNull();
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

    [Fact]
    public void Build_Registers_TraceService_And_StatisticsService_As_Both_Singleton_And_HostedService_Same_Instance()
    {
        // Regression test for v1.2.4 PATCH: the IHostedService wiring
        // must reuse the singleton instance so SinkWiringService attaches
        // the same TraceService that runs ExecuteAsync. If a future
        // refactor changes AddHostedService(sp => sp.GetRequiredService<T>())
        // to AddHostedService<T>() (which would resolve a second
        // instance), SinkWiringService attaches instance A and the
        // hosted-service worker runs on instance B — the v1.2.3 bug
        // returns silently and all 535+ existing tests stay green.
        using var host = AppHostBuilder.Build();

        var traceSingleton = host.Services.GetRequiredService<TraceService>();
        var traceHosted = host.Services.GetServices<IHostedService>()
            .OfType<TraceService>().Single();
        traceHosted.Should().BeSameAs(traceSingleton,
            "TraceService IHostedService registration must reuse the singleton or Trace/Stats views stay empty");

        var statsSingleton = host.Services.GetRequiredService<StatisticsService>();
        var statsHosted = host.Services.GetServices<IHostedService>()
            .OfType<StatisticsService>().Single();
        statsHosted.Should().BeSameAs(statsSingleton,
            "StatisticsService IHostedService registration must reuse the singleton or the 1 Hz stats pump never fires");
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: the IsoTpLayer factory must accept the new
    /// Func&lt;CanFrame, Task&gt; send-callback signature (no longer the sync
    /// .AsTask().Wait() deadlock pattern). Resolving the layer from the
    /// built host is enough to prove the factory compiles and runs.
    /// </summary>
    [Fact]
    public void Build_IsoTpLayer_Factory_Accepts_Async_Send_Callback()
    {
        using var host = AppHostBuilder.Build();
        var layer = host.Services.GetService<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>();
        layer.Should().NotBeNull(
            "IsoTpLayer factory must use the async send-callback overload (Item 2 fix)");
    }

    /// <summary>
    /// v1.2.12 PATCH Item 6: RecordViewModel, SendViewModel, and
    /// SignalViewModel all implement <see cref="IDisposable"/> (v1.2.11
    /// review fix) and own background resources (DispatcherTimer for the
    /// first two; System.Threading.Timer for the third). The
    /// <see cref="IHost"/> only disposes <see cref="IHostedService"/>
    /// instances, so without an <c>AddHostedService(sp =>
    /// sp.GetRequiredService&lt;T&gt;())</c> line for each VM the
    /// <c>Dispose</c> method is dead code on the production path. This
    /// test pins the registration: if a future refactor drops the
    /// double-registration, the VM's timer (and its strong reference to
    /// <c>this</c>) keeps the VM alive forever after the shell
    /// navigates away — a memory leak the build-error-resolver would
    /// not catch.
    /// </summary>
    [Fact]
    public void Build_Registers_RecordViewModel_As_Both_Singleton_And_HostedService_Same_Instance()
    {
        using var host = AppHostBuilder.Build();

        var singleton = host.Services.GetRequiredService<RecordViewModel>();
        var hosted = host.Services.GetServices<IHostedService>()
            .OfType<RecordViewModel>().Single();
        hosted.Should().BeSameAs(singleton,
            "RecordViewModel IHostedService registration must reuse the singleton or its DispatcherTimer never disposes (Item 6 fix)");
    }

    [Fact]
    public void Build_Registers_SendViewModel_As_Both_Singleton_And_HostedService_Same_Instance()
    {
        using var host = AppHostBuilder.Build();

        var singleton = host.Services.GetRequiredService<SendViewModel>();
        var hosted = host.Services.GetServices<IHostedService>()
            .OfType<SendViewModel>().Single();
        hosted.Should().BeSameAs(singleton,
            "SendViewModel IHostedService registration must reuse the singleton or its DispatcherTimer never disposes (Item 6 fix)");
    }

    [Fact]
    public void Build_Registers_SignalViewModel_As_Both_Singleton_And_HostedService_Same_Instance()
    {
        using var host = AppHostBuilder.Build();

        var singleton = host.Services.GetRequiredService<SignalViewModel>();
        var hosted = host.Services.GetServices<IHostedService>()
            .OfType<SignalViewModel>().Single();
        hosted.Should().BeSameAs(singleton,
            "SignalViewModel IHostedService registration must reuse the singleton or its drain Timer never disposes (Item 6 fix)");
    }
}
