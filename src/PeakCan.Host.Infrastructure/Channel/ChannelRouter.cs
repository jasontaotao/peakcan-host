using System.Diagnostics;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Fans frames out from one or more <see cref="ICanChannel"/>s to one or
/// more <see cref="IFrameSink"/>s, with per-sink exception isolation.
/// <para>
/// Sinks are independent: a sink that throws on
/// <see cref="IFrameSink.OnFrame"/> has its exception forwarded to
/// <see cref="IFrameSink.OnError"/> on the same sink, and the next sink
/// still receives the frame. This keeps the read loop on each
/// <see cref="ICanChannel"/> unblocked even when downstream consumers
/// (statistics, UI, decoders) misbehave.
/// </para>
/// <para>
/// Thread-safety: <see cref="RegisterChannel"/>, <see cref="UnregisterChannel"/>,
/// <see cref="AttachSink"/>, and <see cref="DetachSink"/> may be called from
/// any thread; the underlying list mutations are gated by a single
/// <c>lock</c>. Frame dispatch takes a snapshot under the lock then iterates
/// outside it.
/// </para>
/// <para>
/// The per-frame <c>_sinks.ToArray()</c> snapshot allocates a new array on
/// every frame; at 10k fps that is 10k allocations/sec. Acceptable for MVP
/// — a follow-up can switch to a reusable buffer or an
/// <c>ImmutableArray</c> rebuilt only on Attach/Detach.
/// </para>
/// </summary>
public sealed class ChannelRouter : IFrameSource
{
    private readonly List<ICanChannel> _channels = new();
    private readonly List<IFrameSink> _sinks = new();
    private readonly object _gate = new();

    /// <summary>
    /// Subscribe to <paramref name="channel"/>'s <c>FrameReceived</c>.
    /// Idempotent.
    /// <para>
    /// Not on <see cref="IFrameSource"/>: only the multi-source router
    /// needs to manage channel registrations. Single-source consumers use
    /// the channel's <c>FrameReceived</c> event directly.
    /// </para>
    /// </summary>
    public void RegisterChannel(ICanChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            if (_channels.Contains(channel)) return;
            _channels.Add(channel);
            channel.FrameReceived += OnChannelFrame;
        }
    }

    /// <summary>Unsubscribe from <paramref name="channel"/>. Idempotent.</summary>
    public void UnregisterChannel(ICanChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            if (_channels.Remove(channel))
            {
                channel.FrameReceived -= OnChannelFrame;
            }
        }
    }

    /// <summary>Add a sink to the fan-out list. Idempotent.</summary>
    public void AttachSink(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            if (!_sinks.Contains(sink)) _sinks.Add(sink);
        }
    }

    /// <summary>Remove a sink from the fan-out list. Idempotent.</summary>
    public void DetachSink(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sinks.Remove(sink);
        }
    }

    private void OnChannelFrame(CanFrame frame)
    {
        // Snapshot under the lock; iterate outside it so a slow sink does
        // not block Register/Unregister/Attach/Detach on other threads.
        IFrameSink[] snapshot;
        lock (_gate) snapshot = _sinks.ToArray();
        foreach (var s in snapshot)
        {
            try
            {
                s.OnFrame(frame);
            }
            // OperationCanceledException is allowed to propagate so a sink
            // that is mid-shutdown (per ICanChannel's CTS disposal contract)
            // can abort the read loop cleanly. Other exceptions are caught
            // and rerouted to OnError for per-sink isolation.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-sink isolation: surface the failure to the same sink
                // so it can log. Do not propagate to the channel's read
                // loop (that would silently kill traffic for all sinks).
                try
                {
                    s.OnError(ex);
                }
                catch (Exception onErrorEx)
                {
                    // Per spec section 6.2 ("Never silently swallow errors"),
                    // the secondary exception must be observable. The router
                    // does not depend on ILogger yet; use Debug.WriteLine so
                    // the failure is visible under a debugger-attached host.
                    Debug.WriteLine(
                        $"[ChannelRouter] sink {s.GetType().Name} OnError itself threw; auto-detaching. Original: {ex.GetType().Name}: {ex.Message} | Secondary: {onErrorEx.GetType().Name}: {onErrorEx.Message}");
                    DetachSink(s);
                }
            }
        }
    }
}
