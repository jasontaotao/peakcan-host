using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
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
public sealed class DbcDecodeBackgroundService : BackgroundService, IFrameSink
{
    /// <summary>Bounded capacity; matches TraceService's drop semantics.</summary>
    private const int DecodeQueueCapacity = 10_000;

    private readonly DbcService _dbc;
    private readonly SignalViewModel _signalVm;
    private readonly Channel<CanFrame> _queue = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(DecodeQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    public DbcDecodeBackgroundService(DbcService dbc, SignalViewModel signalVm)
    {
        _dbc = dbc ?? throw new ArgumentNullException(nameof(dbc));
        _signalVm = signalVm ?? throw new ArgumentNullException(nameof(signalVm));
    }

    /// <summary>
    /// SDK-thread entrypoint. Non-blocking: enqueues into the bounded
    /// channel. If the worker is behind, the oldest frame is silently
    /// dropped (consistent with TraceService's policy).
    /// </summary>
    public void OnFrame(CanFrame frame) => _queue.Writer.TryWrite(frame);

    /// <summary>Per-sink error hook — logged via Debug.WriteLine.</summary>
    public void OnError(Exception ex)
    {
        Debug.WriteLine(
            $"[DbcDecodeBackgroundService] forwarded error (no action): {ex.GetType().Name}: {ex.Message}");
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
                    if (!doc.MessagesById.TryGetValue(frame.Id.Raw, out var msg)) continue;
                    _signalVm.ApplyFrame(frame, msg);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }
}