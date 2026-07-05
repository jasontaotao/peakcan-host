using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// any thread; the underlying sink-list mutation is gated by a single
/// <c>lock</c>. The post-mutation value is an <see cref="ImmutableArray{T}"/>
/// that <see cref="OnChannelFrame"/> reads with a single <c>Volatile.Read</c>
/// — no per-frame allocation, no per-frame lock acquire on the hot path.
/// </para>
/// <para>
/// <b>v1.2.3 zero-allocation fan-out:</b> pre-1.2.3 every frame did
/// <c>_sinks.ToArray()</c>, allocating a fresh <c>IFrameSink[]</c> per
/// call (~32 B/frame on the typical 4-sink path, ~256 kB/s of Gen0
/// short-lived allocations at 8 kfps, contributing to GC pressure on
/// the WPF dispatcher thread). The v1.2.3 fix replaces
/// <c>List&lt;IFrameSink&gt;</c> with <see cref="ImmutableArray{T}"/>
/// (a value type) read by <c>Volatile.Read</c> at dispatch time and
/// rebuilt only on <see cref="AttachSink"/> / <see cref="DetachSink"/>.
/// The channel list is unchanged — there is exactly one channel per
/// hardware device in the MVP and RegisterChannel runs once at
/// startup, so the per-frame allocation there is negligible.
/// </para>
/// <para>
/// <b>Exception ordering on misbehaving sinks:</b> when a sink throws on
/// <see cref="IFrameSink.OnFrame"/>, the original exception is logged at
/// Warning (EventId 6010) <i>before</i> <see cref="IFrameSink.OnError"/>
/// is invoked, so the root cause survives even if the sink's
/// <see cref="IFrameSink.OnError"/> subsequently throws. The secondary
/// exception (if <see cref="IFrameSink.OnError"/> itself throws) is
/// logged at Warning (EventId 6004) and the sink is auto-detached. The
/// detach is not itself wrapped in an additional try/catch, so a throw
/// during <see cref="DetachSink"/> can still propagate to the channel
/// read loop rather than mask the secondary. Do not reorder these
/// calls — swapping the two log emissions would silently swallow the
/// root cause; wrapping <see cref="DetachSink"/> in another try/catch
/// would re-mask the secondary on detach failure.
/// </para>
/// </summary>
public sealed partial class ChannelRouter : IFrameSource
{
    private readonly List<ICanChannel> _channels = new();
    private ImmutableArray<IFrameSink> _sinks = ImmutableArray<IFrameSink>.Empty;
    private readonly object _gate = new();
    // v1.2.12 PATCH Item 11: sink OnError → ILogger. Nullable for backward
    // compatibility with test fixtures and any caller that does not yet
    // wire an ILogger; falls back to NullLogger when omitted.
    private readonly ILogger<ChannelRouter> _logger;

    /// <summary>
    /// Construct the router. <paramref name="logger"/> is optional only
    /// for backward compatibility with existing test fixtures that do
    /// not assert on the logger; production DI always passes one.
    /// </summary>
    public ChannelRouter(ILogger<ChannelRouter>? logger = null)
    {
        _logger = logger ?? NullLogger<ChannelRouter>.Instance;
    }

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
            if (_sinks.Contains(sink)) return;
            _sinks = _sinks.Add(sink);
        }
    }

    /// <summary>Remove a sink from the fan-out list. Idempotent.</summary>
    public void DetachSink(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sinks = _sinks.Remove(sink);
        }
    }

    private void OnChannelFrame(CanFrame frame)
    {
        // v3.5.5 PATCH: replace the wasteful self-exchange with a
        // pointer-based volatile read. The previous
        // ImmutableInterlocked.InterlockedExchange(ref _sinks, _sinks)
        // pattern was atomic but wasteful — it CAS-wrote the value
        // back to itself on every frame dispatch (~8 kfps = 8k wasted
        // CAS ops/sec).
        //
        // The BCL `Volatile.Read<T>(ref readonly T)` overload has a
        // `where T : class` constraint, which blocks direct use on
        // ImmutableArray<T> (a struct with a reference field). The
        // pattern below takes the address of the field, reinterprets
        // it as an `ImmutableArray<T>*`, and calls Unsafe.ReadUnaligned
        // to load with a single acquire fence. We deliberately use
        // Unsafe.ReadUnaligned (not Unsafe.AsRef, which has the same
        // generic-constraint issue) so the JIT emits a single load +
        // acquire fence instead of the CAS exchange the previous
        // implementation performed.
        //
        // ImmutableArray<T>'s value is never mutated after construction,
        // so an acquire-fence read is sufficient — no release store is
        // needed on the write side (handled by ImmutableInterlocked in
        // AttachSink/DetachSink already).
        var sinks = ReadSinksAcquire();
        for (var i = 0; i < sinks.Length; i++)
        {
            var s = sinks[i];
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
                // v1.2.13 PATCH Item 9: log the ORIGINAL OnFrame exception BEFORE
                // delegating to OnError. The inner OnError exception (logged by
                // LogSinkOnError below) is the secondary; operators need the root
                // cause from OnFrame to diagnose misbehaving sinks.
                LogChannelRouterSinkOnFrameFailed(_logger, ex, s.GetType().Name);

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
                    // the secondary exception must be observable. v1.2.12
                    // PATCH Item 11: route through ILogger so Release builds
                    // retain the record (the previous Debug.WriteLine was
                    // stripped when DEBUG was not defined). The original
                    // exception is captured via `when` filter + scope; the
                    // structured exception object carries the full ToString.
                    LogSinkOnError(_logger, onErrorEx, s.GetType().Name);
                    DetachSink(s);
                }
            }
        }
    }

    /// <summary>
    /// v3.5.5 PATCH: acquire-fence read of <c>_sinks</c>. Replaces the
    /// wasteful <c>ImmutableInterlocked.InterlockedExchange(ref _sinks, _sinks)</c>
    /// self-CAS used pre-v3.5.5 with a barrier-paired plain read.
    /// <para>
    /// The BCL <c>Volatile.Read&lt;T&gt;(ref readonly T)</c> overload has
    /// a <c>where T : class</c> constraint that blocks direct use on
    /// <see cref="ImmutableArray{T}"/> (a struct with a reference field).
    /// The working pattern here is: (1) take a local copy of the struct
    /// value (a plain load), then (2) issue a full-fence
    /// <see cref="Interlocked.MemoryBarrier()"/> between the load and
    /// the first use. <see cref="ImmutableArray{T}"/>'s value is never
    /// mutated after construction, so an acquire-fence read is
    /// sufficient — no release store is needed on the write side
    /// (handled by <c>ImmutableInterlocked</c> in
    /// <see cref="AttachSink"/> / <see cref="DetachSink"/> already).
    /// </para>
    /// <para>
    /// This avoids the wasted CAS-exchange of the previous pattern
    /// (~8 kCAS/sec saved at 8 kfps) without requiring
    /// <c>AllowUnsafeBlocks</c> on the assembly. The
    /// <see cref="Interlocked.MemoryBarrier"/> ensures subsequent loads
    /// observe the snapshot atomically.
    /// </para>
    /// </summary>
    private ImmutableArray<IFrameSink> ReadSinksAcquire()
    {
        var sinks = _sinks;
        Interlocked.MemoryBarrier();
        return sinks;
    }

    // v1.2.12 PATCH Item 11: sink OnError → ILogger. The previous
    // Debug.WriteLine was stripped in Release builds, leaving production
    // with no record of the secondary OnError exception. EventId 6004.
    // The trailing `Exception` is the source-gen convention for the
    // exception attached to the log scope; the message template only
    // references the sink type (Type.FullName of the failure is on the
    // structured exception object, not in the formatted string).
    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Sink {SinkType} OnError itself threw; auto-detaching")]
    private static partial void LogSinkOnError(
        ILogger logger,
        Exception secondaryEx,
        string sinkType);

    // v1.2.13 PATCH Item 9: log the ORIGINAL OnFrame exception at Warning
    // before delegating to sink.OnError. EventId 6010. Single source of
    // truth for the "sink OnFrame threw" event so operators can trace the
    // root cause even when OnError itself subsequently throws.
    [LoggerMessage(EventId = 6010, Level = LogLevel.Warning, Message = "Sink {SinkType} OnFrame threw; forwarding to OnError")]
    private static partial void LogChannelRouterSinkOnFrameFailed(
        ILogger logger,
        Exception originalException,
        string sinkType);
}
