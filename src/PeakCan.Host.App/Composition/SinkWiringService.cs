using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Services;
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
/// three (now four) singletons and the router is ready to accept sinks.
/// A <c>PostConfigureServices</c> approach would not work because the
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
    private readonly DbcDecodeBackgroundService _dbcDecode;
    private readonly RecordService _record;
    private readonly IsoTpSinkAdapter _isoTpAdapter;
    private readonly J1939TpSinkAdapter _j1939Adapter;

    /// <summary>
    /// All seven dependencies are resolved by DI. The router and the
    /// four sinks + the ISO-TP adapter + the J1939 TP adapter are
    /// registered as singletons in <c>AppHostBuilder.Build</c>, so this
    /// service is the only place that ever wires them together.
    /// <para>
    /// P0 (flashing feature 2026-07-21): the <see cref="IsoTpSinkAdapter"/>
    /// closes the long-standing receive-wiring gap — the production UDS
    /// stack could send requests but never received ECU responses because
    /// <see cref="PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer.ProcessFrame"/> had
    /// no production call site and no <c>IFrameSink</c> adapter wired it to
    /// incoming router frames. Attaching the adapter here makes the three
    /// diagnostic tabs (DIDs / Routines / DTCs) finally receive ECU replies.
    /// </para>
    /// <para>
    /// Task 9: the <see cref="J1939TpSinkAdapter"/> does the same for the
    /// J1939 transport-protocol stack — the adapter feeds TP.CM/TP.DT
    /// frames (PGN 0x00EC00/0x00EB00) into
    /// <see cref="PeakCan.HIL.Core.J1939.J1939TpLayer.ProcessFrame"/>,
    /// which otherwise also has no production call site.
    /// </para>
    /// </summary>
    public SinkWiringService(
        ChannelRouter router,
        TraceService trace,
        BusStatisticsCollector stats,
        DbcDecodeBackgroundService dbcDecode,
        RecordService record,
        IsoTpSinkAdapter isoTpAdapter,
        J1939TpSinkAdapter j1939Adapter)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _dbcDecode = dbcDecode ?? throw new ArgumentNullException(nameof(dbcDecode));
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _isoTpAdapter = isoTpAdapter ?? throw new ArgumentNullException(nameof(isoTpAdapter));
        _j1939Adapter = j1939Adapter ?? throw new ArgumentNullException(nameof(j1939Adapter));
    }

    /// <summary>
    /// Attach all sinks to the router. Runs once during
    /// <c>IHost.StartAsync</c>, before <see cref="TraceService.ExecuteAsync"/>
    /// begins its 50 ms tick. After this point, any frame arriving at
    /// the router is fanned out to all consumers; the DBC decode service
    /// runs the dictionary lookup + signal decode on its own worker, off
    /// the SDK read thread, the ISO-TP adapter feeds ECU responses
    /// into the UDS stack via <c>ProcessFrame</c>, and the J1939 TP
    /// adapter feeds TP.CM/TP.DT frames into the J1939 transport layer.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _router.AttachSink(_trace);
        _router.AttachSink(_stats);
        _router.AttachSink(_dbcDecode);
        _router.AttachSink(_record);
        _router.AttachSink(_isoTpAdapter);
        _router.AttachSink(_j1939Adapter);
        return Task.CompletedTask;
    }

    /// <summary>No teardown work — the router's own fan-out list is GC-friendly and the sinks own their own state.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
