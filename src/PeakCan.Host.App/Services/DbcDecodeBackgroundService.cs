using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Consumes raw <see cref="CanFrame"/>s on the SDK read thread
/// (via <see cref="IFrameSink.OnFrame"/>) and runs the DBC lookup +
/// signal decode on its own worker thread, offloading work that
/// previously ran inline on the read loop.
/// <para>
/// <b>Why a service?</b> the SDK read loop must stay non-blocking at
/// ~8 000 fps; any per-frame dictionary lookup + signal-decode +
/// collection mutation budget eats into that. The offload path uses a
/// bounded <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>
/// so back-pressure is bounded — if the worker falls behind, oldest
/// frames are dropped rather than ballooning memory.
/// </para>
/// <para>
/// <b>Lifecycle:</b> the service implements both <see cref="IFrameSink"/>
/// (for the SDK-thread <see cref="OnFrame"/> call) and
/// <see cref="BackgroundService"/> (so <see cref="PeakCan.Host.App.Composition.SinkWiringService"/> can
/// attach it to the <c>ChannelRouter</c> and so the worker
/// thread starts on <see cref="BackgroundService.StartAsync"/>).
/// </para>
/// </summary>
public sealed partial class DbcDecodeBackgroundService : BackgroundService, IFrameSink
{
    /// <summary>Bounded capacity; matches TraceService's drop semantics.</summary>
    private const int DecodeQueueCapacity = 10_000;

    private readonly DbcService _dbc;
    private readonly SignalViewModel _signalVm;
    private readonly TraceViewModel _traceVm;
    private readonly ILogger<DbcDecodeBackgroundService> _logger;
    private readonly Channel<CanFrame> _queue = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(DecodeQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    public DbcDecodeBackgroundService(
        DbcService dbc,
        SignalViewModel signalVm,
        TraceViewModel traceVm,
        ILogger<DbcDecodeBackgroundService> logger)
    {
        _dbc = dbc ?? throw new ArgumentNullException(nameof(dbc));
        _signalVm = signalVm ?? throw new ArgumentNullException(nameof(signalVm));
        _traceVm = traceVm ?? throw new ArgumentNullException(nameof(traceVm));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// SDK-thread entrypoint. Non-blocking: enqueues into the bounded
    /// channel. If the worker is behind, the oldest frame is silently
    /// dropped (consistent with TraceService's policy).
    /// </summary>
    public void OnFrame(CanFrame frame) => _queue.Writer.TryWrite(frame);

    /// <summary>Sink-isolation hook — logs via ILogger. Debug.WriteLine stripped in Release builds; ILogger is not.</summary>
    public void OnError(Exception ex)
    {
        LogSinkError(_logger, ex, nameof(DbcDecodeBackgroundService));
    }

    /// <summary>
    /// Worker loop: drain the queue and run the DBC decode on this
    /// service's thread. <see cref="SignalViewModel.ApplyFrame"/>
    /// marshals the resulting collection mutation to the WPF UI thread.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var frame))
                {
                    var doc = _dbc.Current;
                    if (doc is null) continue;
                    // PEAK's CanId.Raw carries the pure 11/29-bit
                    // identifier (bit 31 = 0, enforced by CanId ctor
                    // validation), but DbcDocument.MessagesById is keyed
                    // by the merged-IDE convention (bit 31 set for
                    // Extended, matching PCAN_MESSAGE_ID). The DBC parser
                    // stores Extended message IDs with the IDE bit set,
                    // e.g. "BO_ 2147487744 X" lands as 0x80001000.
                    // Without this normalization, every Extended frame
                    // misses the dictionary and SignalViewModel stays
                    // empty for any DBC containing an Extended message.
                    //
                    // NOTE: the scripting API
                    // (Scripting/CanApi.cs:217 _messageCallbacks lookup)
                    // intentionally uses the opposite convention — raw
                    // ID, no IDE bit merge — because there script
                    // authors supply the int literal directly. Do not
                    // "consistency-fix" that lookup without revisiting
                    // the script API contract.
                    var lookupId = frame.Id.IsExtended
                        ? frame.Id.Raw | 0x80000000u
                        : frame.Id.Raw;
                    if (!doc.MessagesById.TryGetValue(lookupId, out var msg)) continue;
                    _signalVm.ApplyFrame(frame, msg);

                    // v1.2.11 PATCH Item 2 fan-out: if a TraceEntry is
                    // awaiting DBC decode for this frame, fill its Decoded
                    // string. The lookup key matches the (IdRaw,
                    // TimestampMicroseconds, ChannelHandle) tuple that
                    // TraceViewModel.AppendBatchAsync registered — pure ID
                    // without IDE-merge, since that's what was registered.
                    var pendingKey = new TraceEntryKey(
                        frame.Id.Raw,
                        frame.Timestamp.TotalMicroseconds,
                        frame.Channel.Handle);
                    // TryCompletePending atomically removes the entry on
                    // success so it doesn't linger in the pending map for
                    // the lifetime of the trace (v1.2.11 code review HIGH).
                    if (_traceVm.TryCompletePending(pendingKey, out var traceEntry) && traceEntry is not null)
                    {
                        var decoded = FormatDecoded(msg, frame);
                        // Marshal to UI thread when Application is up so the
                        // WPF DataGrid binding observes the PropertyChanged
                        // on the dispatcher that owns the row. In tests
                        // (no Application) RunOnUiPost falls through to inline.
                        ((Action)(() => traceEntry.Decoded = decoded)).RunOnUiPost();
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        finally
        {
            // LOW fix: complete the writer on shutdown. Without this,
            // OnFrame can still write to the channel after the worker
            // exits, violating the Channel contract. TryWrite succeeds
            // silently but the data is never consumed.
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// v1.2.11 PATCH Item 2: format a DBC message's decoded signals as
    /// "Name=Value, Name=Value, ...". Uses invariant culture so the
    /// Trace Decoded column reads consistently across locales.
    /// </summary>
    internal static string FormatDecoded(Message msg, CanFrame frame)
    {
        if (msg.Signals.Count == 0) return string.Empty;
        var parts = new List<string>(msg.Signals.Count);
        foreach (var signal in msg.Signals)
        {
            var value = SignalDecoder.Decode(frame.Data.Span, signal);
            // G format keeps doubles compact (no trailing zeros); InvariantCulture
            // prevents de-DE locale from emitting "0,42" with a comma.
            parts.Add($"{signal.Name}={value.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(", ", parts);
    }

    // v1.2.12 PATCH Item 11: sink OnError → ILogger. The previous
    // Debug.WriteLine was stripped in Release builds, leaving production
    // with no record of forwarded errors. EventId 6002.
    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "{Service} OnError forwarded")]
    private static partial void LogSinkError(ILogger logger, Exception ex, string service);
}
