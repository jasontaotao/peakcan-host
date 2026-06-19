using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Closes the Task 12 wiring gap. Task 12 registered
/// <see cref="ChannelRouter"/>, <see cref="TraceService"/>, and
/// <see cref="BusStatisticsCollector"/> as DI singletons but did not
/// connect them — without this service, frames arriving at the router
/// are dropped on the floor because no <see cref="IFrameSink"/> is
/// attached.
/// <para>
/// Implemented as an <see cref="IHostedService"/> so it runs during
/// <c>IHost.StartAsync</c>: by then the DI container has resolved all
/// three singletons and the router is ready to accept sinks. A
/// <c>PostConfigureServices</c> approach would not work because the
/// router and sinks are not yet instantiated at registration time; a
/// direct <c>BuildServiceProvider().GetService(...)</c> from
/// <c>AppHostBuilder.Build</c> would work but would break the
/// <c>IHost</c> lifetime (the inner provider would never be disposed).
/// </para>
/// <para>
/// <see cref="ChannelRouter.AttachSink"/> is idempotent (Task 10
/// review), so calling <see cref="StartAsync"/> twice is safe.
/// </para>
/// </summary>
internal sealed class SinkWiringService : IHostedService
{
    private readonly ChannelRouter _router;
    private readonly TraceService _trace;
    private readonly BusStatisticsCollector _stats;

    /// <summary>
    /// All three dependencies are resolved by DI. The router and the
    /// two sinks are registered as singletons in <c>AppHostBuilder.Build</c>,
    /// so this service is the only place that ever wires them together.
    /// </summary>
    public SinkWiringService(ChannelRouter router, TraceService trace, BusStatisticsCollector stats)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
    }

    /// <summary>
    /// Attach both sinks to the router. Runs once during
    /// <c>IHost.StartAsync</c>, before <see cref="TraceService.ExecuteAsync"/>
    /// begins its 50 ms tick. After this point, any frame arriving at
    /// the router is fanned out to both consumers.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _router.AttachSink(_trace);
        _router.AttachSink(_stats);
        return Task.CompletedTask;
    }

    /// <summary>No teardown work — the router's own fan-out list is GC-friendly and the sinks own their own state.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
