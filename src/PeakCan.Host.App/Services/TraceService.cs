using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// <see cref="BackgroundService"/> that batches inbound frames into
/// <see cref="TraceViewModel"/> every 50 ms. Implements
/// <see cref="IFrameSink"/> so the <see cref="ChannelRouter"/> can
/// attach it as a fan-out target.
/// <para>
/// <b>Why batch?</b> a 1 Mbps CAN bus can sustain ~8 000 fps; updating
/// an <c>ObservableCollection</c> per frame would flood the WPF
/// dispatcher with <c>CollectionChanged</c> notifications and peg a
/// CPU. Batching at 50 ms caps the UI work at 20 updates/sec,
/// independent of bus rate.
/// </para>
/// <para>
/// <b>Why a bounded channel?</b> <see cref="OnFrame"/> is called on the
/// SDK read thread; it must be non-blocking. A bounded
/// <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>
/// gives us natural back-pressure: if the UI stalls for &gt;50 ms the
/// oldest unread frames are dropped rather than ballooning memory.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the SDK read thread calls
/// <see cref="OnFrame"/>; the <see cref="ExecuteAsync(CancellationToken)"/>
/// loop drains the channel and dispatches to the UI thread. The
/// channel itself is the synchronization point — no shared state
/// outside the channel is touched from both threads.
/// </para>
/// </summary>
public sealed class TraceService : BackgroundService, IFrameSink
{
    private readonly TraceViewModel _vm;
    private readonly Channel<CanFrame> _batch = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Construct a service bound to <paramref name="vm"/>. VM is the UI-thread target for batched rows.</summary>
    public TraceService(TraceViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
    }

    /// <summary>
    /// Background loop: every 50 ms, drain the channel into a local
    /// list and hand it to the VM. Exits cleanly on cancellation.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buf = new List<CanFrame>(256);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(50, stoppingToken); }
            catch (TaskCanceledException) { break; }
            buf.Clear();
            while (_batch.Reader.TryRead(out var f)) buf.Add(f);
            if (buf.Count > 0) await _vm.AppendBatchAsync(buf);
        }
    }

    /// <summary>
    /// Receives a frame from the <see cref="ChannelRouter"/>. Non-throwing,
    /// non-blocking: <see cref="ChannelWriter{T}.TryWrite"/> on a bounded
    /// <c>DropOldest</c> channel always succeeds (it silently drops the
    /// oldest unread item if the channel is full).
    /// </summary>
    public void OnFrame(CanFrame frame) => _batch.Writer.TryWrite(frame);

    /// <summary>
    /// Sink-isolation contract: <see cref="ChannelRouter"/> forwards
    /// exceptions from peer sinks here. The trace pipeline is a
    /// downstream consumer — the originating sink's failure is the
    /// router's concern, not ours. Mirror the
    /// <see cref="BusStatisticsCollector"/> pattern: log via
    /// <see cref="Debug.WriteLine"/> for debugger-attached hosts and move
    /// on.
    /// </summary>
    public void OnError(Exception ex)
    {
        Debug.WriteLine(
            $"[TraceService] forwarded exception (informational, no action taken): {ex.GetType().Name}: {ex.Message}");
    }
}
