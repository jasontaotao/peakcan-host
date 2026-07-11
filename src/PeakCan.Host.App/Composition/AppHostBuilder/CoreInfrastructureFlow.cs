using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow B: Core infrastructure (v1.2.12 PATCH Item 11 + v3.5.2 PATCH + v3.5.4 PATCH + v0.4.0 + Task 18 + Task T3 + earlier).
    // ChannelRouter + BusStatisticsCollector + ITimerFactory + IChannelProbe + IChannelEnumerator + ICanChannelFactory + IPcanReader.
    // Extracted from Build() verbatim per W11 D5.

    /// <summary>
    /// Register the Core-layer infrastructure services.
    /// Extracted from Build() body as a private helper (W11 R3 mitigation).
    /// </summary>
    private void RegisterCoreInfrastructure(IServiceCollection services)
    {
        // v1.2.12 PATCH Item 11: ChannelRouter now accepts an ILogger<ChannelRouter>
        // so the secondary OnError catch (which auto-detaches misbehaving sinks)
        // is observable in Release builds. The logger is optional in the ctor
        // (NullLogger fallback) but production DI always wires one.
        services.AddSingleton<ChannelRouter>(sp =>
            new ChannelRouter(sp.GetRequiredService<ILogger<ChannelRouter>>()));
        services.AddSingleton<BusStatisticsCollector>();
        // v3.5.2 PATCH: ITimerFactory seam so RecordService +
        // StatisticsService can be unit-tested with a deterministic
        // FakeTimerFactory (no wall-clock dependency). v3.5.4 PATCH:
        // switched to CyclicTimerFactory so the same singleton handles
        // both IPeriodicTimer (RecordService/StatisticsService/TraceService)
        // and ICyclicTimer (CyclicSendService/CyclicDbcSendService). The
        // factory is stateless — only the dispatch shape differs.
        services.AddSingleton<PeakCan.Host.Core.Services.ITimerFactory,
                                      PeakCan.Host.Core.Services.CyclicTimerFactory>();
        // Task 18: extracted PEAK SDK probe call into a swappable
        // service so the App assembly has no Peak.Can.Basic dependency
        // (enforced by LayeringRulesTests.App_Should_Not_Depend_On_Peak_Can_Basic).
        services.AddSingleton<PeakCan.Host.Core.IChannelProbe,
                                       PeakCan.Host.Infrastructure.Peak.PeakChannelProbe>();

        // v0.4.0: multi-channel enumerator. Probes PCAN-USB 1–16.
        services.AddSingleton<PeakCan.Host.Core.IChannelEnumerator,
                                       PeakCan.Host.Infrastructure.Peak.PeakChannelEnumerator>();

        // Task T3 (H4): the App-layer VM no longer news PeakCanChannel
        // directly; it asks the factory for an ICanChannel. Production DI
        // binds the PEAK implementation; tests inject a fake to drive the
        // connect/disconnect state machine without hardware.
        services.AddSingleton<PeakCan.Host.Core.IChannelFactory,
                                      PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>();

        // v0.4.0: IPcanReader abstracts the PEAK SDK read calls so
        // PeakCanChannel's read loop can be unit-tested without hardware.
        services.AddSingleton<PeakCan.Host.Infrastructure.Peak.IPcanReader,
                                      PeakCan.Host.Infrastructure.Peak.PcanReader>();
    }
}