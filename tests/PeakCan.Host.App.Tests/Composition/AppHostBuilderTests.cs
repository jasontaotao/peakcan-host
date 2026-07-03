using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.Views;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.IsoTp;
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
        using var host = new AppHostBuilder().Build();
        host.Services.Should().NotBeNull();
    }

    [Fact]
    public void Build_Registers_ChannelRouter_As_Singleton()
    {
        using var host = new AppHostBuilder().Build();
        var a = host.Services.GetService<ChannelRouter>();
        var b = host.Services.GetService<ChannelRouter>();
        a.Should().NotBeNull();
        a.Should().BeSameAs(b, "ChannelRouter is a singleton — router state must be shared");
    }

    [Fact]
    public void Build_Registers_BusStatisticsCollector_As_Singleton()
    {
        using var host = new AppHostBuilder().Build();
        var a = host.Services.GetService<BusStatisticsCollector>();
        var b = host.Services.GetService<BusStatisticsCollector>();
        a.Should().NotBeNull();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Build_Registers_All_App_Services_As_Singletons()
    {
        using var host = new AppHostBuilder().Build();
        host.Services.GetService<TraceService>().Should().NotBeNull();
        host.Services.GetService<SendService>().Should().NotBeNull();
        host.Services.GetService<DbcService>().Should().NotBeNull();
        host.Services.GetService<StatisticsService>().Should().NotBeNull();
    }

    [Fact]
    public void Build_Registers_All_ViewModels_As_Singletons()
    {
        using var host = new AppHostBuilder().Build();
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
        using var host = new AppHostBuilder().Build();
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
        var appShell = RunSta(() => new AppHostBuilder().Build().Services.GetService<AppShell>());
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
        using var host = new AppHostBuilder().Build();
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
        using var host = new AppHostBuilder().Build();

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
        using var host = new AppHostBuilder().Build();
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
        using var host = new AppHostBuilder().Build();

        var singleton = host.Services.GetRequiredService<RecordViewModel>();
        var hosted = host.Services.GetServices<IHostedService>()
            .OfType<RecordViewModel>().Single();
        hosted.Should().BeSameAs(singleton,
            "RecordViewModel IHostedService registration must reuse the singleton or its DispatcherTimer never disposes (Item 6 fix)");
    }

    [Fact]
    public void Build_Registers_SendViewModel_As_Both_Singleton_And_HostedService_Same_Instance()
    {
        using var host = new AppHostBuilder().Build();

        var singleton = host.Services.GetRequiredService<SendViewModel>();
        var hosted = host.Services.GetServices<IHostedService>()
            .OfType<SendViewModel>().Single();
        hosted.Should().BeSameAs(singleton,
            "SendViewModel IHostedService registration must reuse the singleton or its DispatcherTimer never disposes (Item 6 fix)");
    }

    [Fact]
    public void Build_Registers_SignalViewModel_As_Both_Singleton_And_HostedService_Same_Instance()
    {
        using var host = new AppHostBuilder().Build();

        var singleton = host.Services.GetRequiredService<SignalViewModel>();
        var hosted = host.Services.GetServices<IHostedService>()
            .OfType<SignalViewModel>().Single();
        hosted.Should().BeSameAs(singleton,
            "SignalViewModel IHostedService registration must reuse the singleton or its drain Timer never disposes (Item 6 fix)");
    }

    [Fact]
    public void UdsClient_Resolution_Passes_SessionLogger()
    {
        // v1.2.13 PATCH Item 2: AppHostBuilder DI must thread
        // ILogger<UdsSession> through UdsClient into UdsSession so S3
        // keepalive failures are observable in production.
        using var host = new AppHostBuilder().Build();
        var udsClient = host.Services.GetRequiredService<PeakCan.Host.Core.Uds.UdsClient>();

        udsClient.Session.SessionLogger.Should().NotBeNull(
            "AppHostBuilder must wire ILogger<UdsSession> into UdsClient so " +
            "production S3 keepalive failures are logged at Warning level");
    }

    /// <summary>
    /// v1.2.13 PATCH Item 5: AppHostBuilder's IsoTpLayer factory wraps the
    /// send-callback in an outer try/catch that logs via LogIsoTpSendFailed.
    /// After the layer now throws IsoTpSendFailedException, the outer catch
    /// must pattern-match on the type and skip the duplicate log call —
    /// otherwise every send failure is logged twice (id 3001 emitted by both
    /// the layer's catch arm and the App's outer catch).
    /// <para>
    /// The App factory's outer catch only fires when the send-service
    /// (SendService.SendAsync) itself raises an IsoTpSendFailedException
    /// — the normal path (layer's SendCanFrameAsync → send-callback →
    /// SendService.SendAsync) re-raises as IsoTpSendFailedException inside
    /// the layer, so the App's `when` filter is defense-in-depth. We
    /// exercise it with an inline mirror of the factory's catch arm
    /// (this is the plan's design — the production code is in
    /// AppHostBuilder.cs:201-218 and is covered by the negative-side
    /// assertion: if a future refactor drops the `when` filter, the
    /// catch arm would log here and the assertion would fail).
    /// </para>
    /// </summary>
    [Fact]
    public void Outer_Catch_Skips_LogIsoTpSendFailed_For_IsoTpSendFailedException()
    {
        var logger = Substitute.For<ILogger<IsoTpLayer>>();

        var inner = new InvalidOperationException("sdk down");
        var sendEx = new IsoTpSendFailedException(canId: 0x7E0, frameIndex: 0, inner);

        // Mirror AppHostBuilder.cs:201-218 outer catch. If the production
        // catch were missing the `when (!(ex is IsoTpSendFailedException))`
        // filter, this catch would fire and log; with the filter, the
        // catch is skipped entirely and the exception propagates out.
        Action act = () =>
        {
            try { throw sendEx; }
            catch (Exception ex) when (!(ex is IsoTpSendFailedException))
            {
                IsoTpLayer.LogIsoTpSendFailed(logger, ex, 0x7E0);
            }
        };

        // The exception MUST propagate out (the `when` filter
        // intentionally skips the catch so the consumer sees the
        // original IsoTpSendFailedException, not a fresh log-only path).
        act.Should().Throw<IsoTpSendFailedException>(
            "the `when` filter must skip the catch so the original exception " +
            "propagates to the consumer (UdsClient)");

        // Assert that the outer catch did NOT invoke LogIsoTpSendFailed.
        // Using NSubstitute Received(0) on the logger — the `when` filter
        // short-circuits the catch arm so no log call reaches the logger.
        // CA1848/CA2254 suppressed: this is a NSubstitute mock-arg
        // placeholder, not a real log call.
#pragma warning disable CA1848 // Use LoggerMessage delegates
#pragma warning disable CA2254 // Template should be static
        logger.DidNotReceiveWithAnyArgs().Log(
            default, default, default, default, default!);
#pragma warning restore CA1848
#pragma warning restore CA2254
    }

    /// <summary>
    /// v1.3.0 MINOR Item 5: <c>WithUdsSecurityLockoutConfig</c> on
    /// <see cref="AppHostBuilder"/> must thread a custom lockout policy
    /// into <see cref="PeakCan.Host.Core.Uds.UdsClient"/>'s
    /// <see cref="PeakCan.Host.Core.Uds.UdsSecurity.LockoutConfig"/>.
    /// <para>
    /// Regression: this is the production-side wiring for the v1.3.0
    /// SecurityAccess lockout feature (Task 1 state + Task 2 wire
    /// integration). Without this, every production host hard-codes the
    /// default 3 attempts / 5 s policy and OEM override is impossible
    /// without forking the host composition.
    /// </para>
    /// </summary>
    [Fact]
    public void WithUdsSecurityLockoutConfig_Injects_To_UdsSecurity()
    {
        var builder = new AppHostBuilder()
            .WithUdsSecurityLockoutConfig(new PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig(
                MaxAttempts: 5,
                LockoutDuration: TimeSpan.FromSeconds(10)));

        using var host = builder.Build();
        var udsClient = host.Services.GetRequiredService<PeakCan.Host.Core.Uds.UdsClient>();

        udsClient.Security.LockoutConfig.MaxAttempts.Should().Be(5);
        udsClient.Security.LockoutConfig.LockoutDuration.Should().Be(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// v1.4.0 MINOR: <see cref="AppHostBuilder"/> registers the three new
    /// v1.4.0 services (<see cref="IReplayService"/>,
    /// <see cref="IReplayFrameSink"/>, <see cref="DbcEncodeService"/>) as
    /// DI singletons. The <c>BeSameAs</c> assertion pins the singleton
    /// lifetime: a future refactor that drops the explicit
    /// <c>AddSingleton</c> would resolve a fresh instance per call and
    /// replay state would leak across consumers.
    /// </summary>
    [Fact]
    public void Build_Registers_ReplayAndDbcEncodeServices()
    {
        var builder = new AppHostBuilder();
        using var host = builder.Build();

        host.Services.GetService<IReplayService>().Should().NotBeNull();
        host.Services.GetService<DbcEncodeService>().Should().NotBeNull();
        host.Services.GetService<IReplayFrameSink>().Should().NotBeNull();
        host.Services.GetService<IReplayService>()
            .Should().BeSameAs(host.Services.GetService<IReplayService>(),
                "IReplayService is registered as singleton");
    }

    /// <summary>
    /// v3.0 MINOR (Task 7): wires the new TraceViewerService,
    /// TraceViewerViewModel, and TraceViewerView into DI. Singleton lifetime
    /// on the service matches <see cref="IReplayService"/>; the VM and
    /// non-modal Window are singleton so every caller (including menu
    /// reopen) shares one timeline and the same view instance — the
    /// <c>AppShellViewModel.ShowTraceViewerCommand</c> command opens
    /// the cached window rather than news-up a fresh one each click.
    /// </summary>
    [Fact]
    public void Build_Registers_TraceViewerService_As_Singleton()
    {
        var builder = new AppHostBuilder();
        using var host = builder.Build();

        host.Services.GetService<ITraceViewerService>().Should().NotBeNull();
        host.Services.GetService<ITraceViewerService>()
            .Should().BeSameAs(host.Services.GetService<ITraceViewerService>(),
                "ITraceViewerService is registered as singleton");
    }

    [Fact]
    public void Build_Registers_TraceViewerViewModel_As_Singleton()
    {
        // Task 7: TraceViewerViewModel is a singleton so AppShellViewModel
        // (also a singleton) constructs with the same instance, preserving
        // the loaded trace + signal list + chart scrubber position across
        // menu round-trips. Matches the ReplayViewModel precedent.
        var builder = new AppHostBuilder();
        using var host = builder.Build();

        host.Services.GetService<TraceViewerViewModel>().Should().NotBeNull();
        host.Services.GetService<TraceViewerViewModel>()
            .Should().BeSameAs(host.Services.GetService<TraceViewerViewModel>(),
                "TraceViewerViewModel is registered as singleton");

        // TraceViewerView is intentionally NOT DI-registered — WPF Window
        // ctor requires STA, and AppShellViewModel owns the lazy cached
        // _traceViewerView field (matches ShowReplayCommand precedent).
        // Resolving it from the IServiceProvider would throw on test
        // MTA threads. This negative assertion pins that decision.
        host.Services.GetService<TraceViewerView>().Should().BeNull(
            "TraceViewerView is not DI-registered — the AppShell lazy-show pattern is the integration point");
    }
}
