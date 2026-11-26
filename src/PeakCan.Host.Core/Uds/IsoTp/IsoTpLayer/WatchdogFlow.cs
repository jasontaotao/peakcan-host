using System.Threading;

namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow E: Watchdog (v1.2.13 PATCH Item 1).
    // _rxWatchdog private field + nested WatchdogHandle class +
    // StartReceiveWatchdog + CancelReceiveWatchdog methods moved verbatim
    // from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - StartReceiveWatchdog <- HandleFirstFrame (Flow D) + HandleConsecutiveFrameLocked (Flow D)
    //   - CancelReceiveWatchdog <- Reset (Flow A) + HandleFirstFrame (Flow D) + HandleConsecutiveFrameLocked (Flow D) + StartReceiveWatchdog (intra-flow)
    //
    // R3 (W8 lesson extension to W9): _rxWatchdog field + WatchdogHandle nested
    // class are private and only used by the 2 watchdog methods below.
    // partial-class visibility makes this transparent to compile + test.
    // _watchdogDisposalDeferredCount stays in main (read by tests via
    // [InternalsVisibleTo("PeakCan.HIL.Core.Tests")]).

    /// <summary>
    /// v1.2.13 PATCH Item 1: opaque handle so cancel + Dispose can be
    /// decoupled from CTS lifecycle (the Register callback may still be
    /// holding _rxLock when we cancel; the synchronous Dispose was
    /// racing it). RefCount lets a new handle replace the old one without
    /// prematurely disposing the in-flight CTS.
    ///
    /// Supersession is decided by INSTANCE IDENTITY: the Register callback
    /// short-circuits unless <c>ReferenceEquals(_rxWatchdog, handle)</c>.
    /// This covers both re-arm paths:
    /// <list type="bullet">
    ///   <item>CF→CF re-arm (HandleConsecutiveFrame calls
    ///         StartReceiveWatchdog again): the previous CF's handle is no
    ///         longer the published one, so its callback cannot clear the
    ///         NEW CF's buffer.</item>
    ///   <item>FF→FF interruption (HandleFirstFrame cancels the old FF
    ///         watchdog and installs a NEW one): CancelReceiveWatchdog sets
    ///         <c>_rxWatchdog = null</c> under <c>_rxLock</c> BEFORE calling
    ///         <c>old.Cts.Cancel()</c>, so the old callback (which runs
    ///         synchronously inside Cancel) finds a null / different handle
    ///         and short-circuits without touching <c>_rxInProgress</c> /
    ///         <c>_rxBuffer</c> of the new FF.</item>
    /// </list>
    /// An earlier design compared a per-arm generation counter derived from
    /// <c>_rxExpectedSequence</c>; that sequence wraps with <c>&amp; 0x0F</c>,
    /// so a counter could in principle collide with a stale callback. Identity
    /// comparison has no such aliasing and needs no counter.
    /// </summary>
    private WatchdogHandle? _rxWatchdog;

    /// <summary>
    /// v1.2.13 PATCH Item 1: sealed nested class — see field doc above.
    /// The CTS field is intentionally not exposed via IDisposable:
    /// ownership is delegated to <see cref="CancelReceiveWatchdog"/>
    /// which defers Dispose to the threadpool once RefCount hits 0.
    /// </summary>
#pragma warning disable CA1001
    private sealed class WatchdogHandle
#pragma warning restore CA1001
    {
        public readonly CancellationTokenSource Cts;
        public int RefCount;

        public WatchdogHandle()
        {
            Cts = new CancellationTokenSource();
            // RefCount starts at 0; CancelReceiveWatchdog does +1 (0→1) and
            // the ThreadPool worker does -1 (1→0) then Disposes the CTS.
            // The handle is "armed" by being published to _rxWatchdog, not
            // by RefCount itself.
            RefCount = 0;
        }
    }

    /// <summary>
    /// Arm a CancellationTokenSource that fires after <see cref="ReceiveTimeout"/>.
    /// On expiry it clears _rxInProgress / _rxBuffer so the next FF starts a
    /// fresh reassembly (rather than silently wedging the receive state).
    /// </summary>
    private void StartReceiveWatchdog()
    {
        CancelReceiveWatchdog();

        var timeout = _receiveTimeout;
        var handle = new WatchdogHandle();
        var token = handle.Cts.Token;
        token.Register(() =>
        {
            // Supersession guard: only the handle currently published to
            // _rxWatchdog may clear the reassembly state.
            //
            // Covers both re-arm paths:
            //   1. FF→FF: CancelReceiveWatchdog nulls _rxWatchdog before
            //      cancelling the old CTS, so the old callback sees a
            //      different (or null) _rxWatchdog and short-circuits — the
            //      new FF owns _rxInProgress / _rxBuffer.
            //   2. CF→CF: HandleConsecutiveFrame publishes a brand-new handle,
            //      so the previous CF's callback no longer matches.
            //
            // Identity (not a generation counter) is authoritative: the old
            // generation scheme derived from _rxExpectedSequence, which wraps
            // with & 0x0F, so a stale callback could carry the same number as
            // the live watchdog and wrongly clear the new buffer.
            lock (_rxLock)
            {
                if (!ReferenceEquals(_rxWatchdog, handle))
                    return; // superseded by a newer FF or CF — it owns the state
                if (!_rxInProgress)
                    return;
                _rxInProgress = false;
                _rxBuffer = null;
            }
        });

        lock (_rxLock)
        {
            // Caller invariant: StartReceiveWatchdog is only called while
            // _rxInProgress is true, so the generation ALWAYS advances.
            _rxWatchdog = handle;
        }

        // Arm AFTER publishing: the callback above short-circuits when
        // _rxWatchdog is not this handle, so arming before the publish would
        // let an extremely short N_Cr fire into a "not yet mine" state and be
        // silently dropped. Guard the dispose race like CancelReceiveWatchdog
        // does — a concurrent Cancel may have released the CTS already.
        try { handle.Cts.CancelAfter(timeout); }
        catch (ObjectDisposedException) { /* superseded concurrently */ }
    }

    private void CancelReceiveWatchdog()
    {
        WatchdogHandle? old;
        lock (_rxLock)
        {
            old = _rxWatchdog;
            _rxWatchdog = null;
        }
        if (old is null) return;

        try { old.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }

        // Defer Dispose to the threadpool so any in-flight Register
        // callback (still holding _rxLock) finishes first. RefCount
        // protects against double-decrement from a future Start/Cancel pair.
        Interlocked.Increment(ref old.RefCount);
        Interlocked.Increment(ref _watchdogDisposalDeferredCount);
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var h = (WatchdogHandle)state!;
            if (Interlocked.Decrement(ref h.RefCount) == 0)
                h.Cts.Dispose();
        }, old);
    }
}