using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Composite;
using PeakCan.Host.Infrastructure.Statistics;
using PeakCan.Host.Infrastructure.Zlg;

namespace PeakCan.Host.App.Composition;

public partial class AppHostBuilder
{
    // Flow B: Core infrastructure (v1.2.12 PATCH Item 11 + v3.5.2 PATCH + v3.5.4 PATCH + v0.4.0 + Task 18 + Task T3 + earlier).
    // ChannelRouter + BusStatisticsCollector + ITimerFactory + IChannelProbe + IChannelEnumerator + ICanChannelFactory + IPcanReader.

    private void RegisterCoreInfrastructure(IServiceCollection services)
    {
        services.AddSingleton<ChannelRouter>(sp =>
            new ChannelRouter(sp.GetRequiredService<ILogger<ChannelRouter>>()));
        services.AddSingleton<BusStatisticsCollector>();
        services.AddSingleton<PeakCan.HIL.Core.Services.ITimerFactory,
                                      PeakCan.HIL.Core.Services.CyclicTimerFactory>();

        // === PEAK 基础设施 ===
        services.AddSingleton<PeakCan.Host.Infrastructure.Peak.PeakChannelProbe>();
        services.AddSingleton<PeakCan.Host.Infrastructure.Peak.PeakChannelEnumerator>();
        services.AddSingleton<PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>();
        services.AddSingleton<PeakCan.Host.Infrastructure.Peak.IPcanReader,
                                      PeakCan.Host.Infrastructure.Peak.PcanReader>();

        // === ZLG 基础设施 ===
        services.AddSingleton<ZlgDeviceManager>();
        services.AddSingleton<ZlgChannelProbe>();
        services.AddSingleton<ZlgChannelEnumerator>();
        services.AddSingleton<ZlgCanChannelFactory>();

        // === Composite（按具体类型解析，避免 GetServices<T>() 自引用递归）===
        services.AddSingleton<PeakCan.HIL.Core.IChannelProbe>(
            sp => new CompositeChannelProbe(
                new PeakCan.HIL.Core.IChannelProbe[]
                {
                    sp.GetRequiredService<PeakCan.Host.Infrastructure.Peak.PeakChannelProbe>(),
                    sp.GetRequiredService<ZlgChannelProbe>(),
                }));

        services.AddSingleton<PeakCan.HIL.Core.IChannelEnumerator>(
            sp => new CompositeChannelEnumerator(
                new PeakCan.HIL.Core.IChannelEnumerator[]
                {
                    sp.GetRequiredService<PeakCan.Host.Infrastructure.Peak.PeakChannelEnumerator>(),
                    sp.GetRequiredService<ZlgChannelEnumerator>(),
                }));

        services.AddSingleton<PeakCan.HIL.Core.IChannelFactory>(
            sp => new CompositeChannelFactory(
                new PeakCan.HIL.Core.IChannelFactory[]
                {
                    sp.GetRequiredService<PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>(),
                    sp.GetRequiredService<ZlgCanChannelFactory>(),
                }));
    }
}