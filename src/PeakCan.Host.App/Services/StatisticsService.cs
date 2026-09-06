using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Services;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Services;

/// <summary>
/// 1 Hz timer that snapshots the <see cref="BusStatisticsCollector"/>
/// and pushes the result into <see cref="StatsViewModel"/>. Runs as
/// an <see cref="IHostedService"/> so its lifetime is owned by the
/// <c>IHost</c> (start at app launch, stop at host dispose).
/// <para>
/// <b>Why a separate <c>BackgroundService</c>?</b> the collector is a
/// pure <see cref="Infrastructure.Channel.IFrameSink"/> and does not
/// know about the UI; the VM is bound to a WPF tab. The service is
/// the only place that crosses the boundary on a fixed cadence — same
/// pattern as <see cref="TraceService"/> (Task 13) which drains
/// frames every 50 ms.
/// </para>
/// <para>
/// <b>Tick rate:</b> 1 Hz is intentionally slow. The bus-load
/// heuristic in <see cref="BusStatisticsCollector.LoadPercent"/>
/// already operates over a 1-second window, so faster sampling
/// would just produce overlapping points. 1 Hz matches the rolling
/// 60-point chart's 1-minute window (60 samples × 1 s = 1 min).
/// </para>
/// <para>
/// <b>Cancellation:</b> the loop catches
/// <see cref="OperationCanceledException"/> on the timer wait and
/// exits cleanly. <see cref="StopAsync"/> triggers the cancellation
/// via the host's <c>stoppingToken</c>. The pending
/// <see cref="StatsViewModel.Push"/> call is fire-and-forget on the
/// dispatcher (Task 16 pattern), so no extra teardown is required.
/// </para>
/// </summary>
public sealed partial class StatisticsService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly BusStatisticsCollector _collector;
    private readonly StatsViewModel _vm;
    private readonly ILogger<StatisticsService> _logger;
    // v3.5.2 PATCH: hold the factory, not a concrete PeriodicTimer, so
    // unit tests can drive the snapshot tick deterministically (see
    // FakeTimerFactory in App.Tests). Default ctor wires a real
    // PeriodicTimer-backed factory for production; internal ctor lets
    // tests inject a fake.
    private readonly ITimerFactory _timerFactory;

    public StatisticsService(
        BusStatisticsCollector collector,
        StatsViewModel vm,
        ILogger<StatisticsService> logger)
        : this(collector, vm, logger, new PeriodicTimerFactory())
    {
    }

    /// <summary>
    /// v3.5.2 PATCH: internal ctor lets unit tests inject an
    /// <see cref="ITimerFactory"/> (typically a <c>FakeTimerFactory</c>)
    /// so the "snapshot after one tick" test can advance time without
    /// a real <see cref="System.Threading.PeriodicTimer"/>. Production
    /// DI uses the public 3-arg ctor above, which constructs a real
    /// <see cref="PeriodicTimerFactory"/>.
    /// </summary>
    internal StatisticsService(
        BusStatisticsCollector collector,
        StatsViewModel vm,
        ILogger<StatisticsService> logger,
        ITimerFactory timerFactory)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
    }

    /// <summary>
    /// Loop: wait <see cref="TickInterval"/>, snapshot the collector,
    /// push into the VM. Exits on cancellation.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, TickInterval);
        // v3.5.2 PATCH: factory-supplied timer so tick is
        // test-deterministic (FakeTimerFactory.Fire()) when wired by
        // the internal test-only ctor. await using because
        // IPeriodicTimer is IAsyncDisposable.
        await using var timer = _timerFactory.CreateTimer(TickInterval);
        try
        {
            // Take the first snapshot immediately so the UI shows real
            // numbers before the first tick; otherwise the user sees
            // 60 zeros for a second.
            _vm.Push(_collector.Snapshot());
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _vm.Push(_collector.Snapshot());
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "StatisticsService started, tick={TickSeconds}s")]
    private static partial void LogStarted(ILogger logger, TimeSpan tickSeconds);
}
