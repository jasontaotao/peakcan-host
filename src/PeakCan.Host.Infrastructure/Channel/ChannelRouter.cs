using System.Collections.Immutable;
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
/// <c>lock</c>. The post-mutation value is an <see cref="IFrameSink"/>?
/// array that <see cref="OnChannelFrame"/> reads with a single
/// <c>Volatile.Read</c> — no per-frame lock acquire on the hot path.
/// </para>
/// <para>
/// <b>v3.5.7 PATCH:</b> <c>_sinks</c> field type changed from
/// <see cref="ImmutableArray{T}"/> (struct with reference field, can't
/// be marked <c>volatile</c>) to <c>IFrameSink[]?</c> (reference type,
/// directly usable with <c>Volatile.Read</c> / <c>Volatile.Write</c>).
/// The previous v3.5.5 <c>ReadSinksAcquire</c> helper (plain struct load
/// + <c>Interlocked.MemoryBarrier()</c>) had two flaws: (a) the
/// placement-after-load does not actually constitute an acquire fence
/// (JIT can reorder subsequent reads across it), and (b) the
/// accompanying inline comment claimed the write side used
/// <c>ImmutableInterlocked</c> when in fact <see cref="AttachSink"/> /
/// <see cref="DetachSink"/> were plain stores — so neither side had a
/// proper fence. v3.5.7 fixes both: write side uses <c>Volatile.Write</c>
/// (release fence), read side uses <c>Volatile.Read</c> (acquire fence).
/// Attach/Detach allocates a new array, but that's a registration-time
/// path (not hot) — per-frame read stays allocation-free.
/// </para>
/// <para>
/// <b>v1.2.3 zero-allocation fan-out:</b> pre-1.2.3 every frame did
/// <c>_sinks.ToArray()</c>, allocating a fresh <c>IFrameSink[]</c> per
/// call (~32 B/frame on the typical 4-sink path, ~256 kB/s of Gen0
/// short-lived allocations at 8 kfps, contributing to GC pressure on
/// the WPF dispatcher thread). The v1.2.3 fix replaced
/// <c>List&lt;IFrameSink&gt;</c> with an immutable snapshot
/// (<see cref="ImmutableArray{T}"/> v3.5.5-or-earlier, <c>IFrameSink[]</c>
/// v3.5.7+) read by <c>Volatile.Read</c> at dispatch time and rebuilt
/// only on <see cref="AttachSink"/> / <see cref="DetachSink"/>. The
/// channel list is unchanged — there is exactly one channel per
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
    // v3.5.7 PATCH: IFrameSink[]? (reference type) — enables Volatile.Read
    // / Volatile.Write with proper acquire/release fences. The previous
    // ImmutableArray<IFrameSink> (struct with reference field) cannot
    // be marked volatile, and plain store + post-load Interlocked.
    // MemoryBarrier() is not a true acquire fence (see class-level
    // doc-comment). The empty sentinel below avoids null-checks on the
    // hot path while still allowing Volatile.Write of a null array when
    // the last sink detaches.
    private static readonly IFrameSink[] EmptySinks = Array.Empty<IFrameSink>();
    private IFrameSink[]? _sinks;
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
            var current = _sinks ?? EmptySinks;
            // Linear scan is fine: AttachSink runs at registration time
            // (typically 4-5 sinks, once per app lifetime), not per-frame.
            if (Array.IndexOf(current, sink) >= 0) return;
            var next = new IFrameSink[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = sink;
            // v3.5.7 PATCH: Volatile.Write gives the publish a release
            // fence — OnChannelFrame's Volatile.Read sees the new array
            // with all prior writes (the Array.Copy + last-element
            // assignment) visible.
            Volatile.Write(ref _sinks, next);
        }
    }

    /// <summary>Remove a sink from the fan-out list. Idempotent.</summary>
    public void DetachSink(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            var current = _sinks;
            if (current is null || current.Length == 0) return;
            var idx = Array.IndexOf(current, sink);
            if (idx < 0) return;
            // Replace the array with a copy that omits the sink. If we
            // just removed the last sink, publish null so OnChannelFrame
            // can fast-path on `_sinks is null` (rare in practice — most
            // apps have at least one persistent sink).
            IFrameSink[]? next = (current.Length == 1)
                ? null
                : new IFrameSink[current.Length - 1];
            if (next is not null)
            {
                Array.Copy(current, 0, next, 0, idx);
                Array.Copy(current, idx + 1, next, idx, current.Length - idx - 1);
            }
            Volatile.Write(ref _sinks, next);
        }
    }

    private void OnChannelFrame(CanFrame frame)
    {
        // v3.5.7 PATCH: Volatile.Read on a reference-type field is the
        // canonical acquire-fence read — single load, no constraint
        // gymnastics, no per-frame Interlocked.MemoryBarrier. Replaces
        // the v3.5.5 ReadSinksAcquire helper which had two flaws:
        // (1) post-load barrier placement is not a real acquire fence
        //     (JIT can reorder subsequent reads across the barrier), and
        // (2) the inline comment claimed a write-side fence existed
        //     when in fact AttachSink/DetachSink were plain stores.
        // Both ends now have matching fences via Volatile.Read/Write.
        var sinks = Volatile.Read(ref _sinks) ?? EmptySinks;
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

    // v3.5.7 PATCH: ReadSinksAcquire helper removed — replaced by direct
    // Volatile.Read(ref _sinks) in OnChannelFrame. The helper had two
    // flaws (post-load barrier placement + lying comment about write
    // side) that together made the v3.5.5 "fix" a correctness regression
    // from "wasteful but sound" to "cheap but unsound". See class-level
    // doc-comment + OnChannelFrame for the replacement pattern.

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
