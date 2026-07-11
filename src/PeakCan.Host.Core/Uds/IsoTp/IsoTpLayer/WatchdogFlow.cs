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
    // [InternalsVisibleTo("PeakCan.Host.Core.Tests")]).

    /// <summary>
    /// v1.2.13 PATCH Item 1: opaque handle so cancel + Dispose can be
    /// decoupled from CTS lifecycle (the Register callback may still be
    /// holding _rxLock when we cancel; the synchronous Dispose was
    /// racing it). Generation + RefCount let a new handle replace the
    /// old one without prematurely disposing the in-flight CTS.
    ///
    /// Two distinct re-arm scenarios are protected by different
    /// mechanisms:
    /// <list type="bullet">
    ///   <item>CF→CF re-arm (HandleConsecutiveFrame fires
    ///         StartReceiveWatchdog again with a NEW generation): the
    ///         Generation check inside the Register callback prevents
    ///         the OLD CF watchdog from clearing the NEW CF's buffer —
    ///         each CF re-arm bumps Generation (the per-arm value comes
    ///         from <c>_rxExpectedSequence</c>, which changes on every
    ///         CF boundary).</item>
    ///   <item>FF→FF interruption (HandleFirstFrame cancels the old FF
    ///         watchdog and installs a NEW one BEFORE the old one fires):
    ///         BOTH FFs share Generation=1 (FF always re-arms at gen 1)
    ///         so the Generation check is a no-op here. Instead,
    ///         CancelReceiveWatchdog sets <c>_rxWatchdog = null</c> under
    ///         <c>_rxLock</c> BEFORE calling <c>old.Cts.Cancel()</c>; the
    ///         old Register callback (still queued by the cancellation
    ///         propagation) then takes <c>_rxLock</c>, finds
    ///         <c>_rxWatchdog == null</c>, and short-circuits without
    ///         touching <c>_rxInProgress</c> / <c>_rxBuffer</c> of the
    ///         new FF.</item>
    /// </list>
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
        /// <summary>
        /// CF→CF re-arm marker. Each CF re-arm
        /// (<see cref="HandleConsecutiveFrame"/> calling
        /// <c>StartReceiveWatchdog</c>) bumps this to a fresh value
        /// (sourced from <c>_rxExpectedSequence</c>) so an in-flight
        /// Register callback for the previous CF's watchdog can tell it
        /// has been superseded. NOTE: this Generation check is the
        /// only protection for the CF→CF re-arm path. The FF→FF
        /// interruption path does NOT rely on Generation (both FFs
        /// share gen 1) — see the <c>_rxWatchdog</c> field doc for the
        /// null-state guard that protects FF→FF.
        /// </summary>
        public readonly int Generation;
        public int RefCount;

        public WatchdogHandle(int generation)
        {
            Cts = new CancellationTokenSource();
            Generation = generation;
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
    private void StartReceiveWatchdog(int expectedGeneration)
    {
        CancelReceiveWatchdog();

        var timeout = _receiveTimeout;
        var handle = new WatchdogHandle(expectedGeneration);
        var token = handle.Cts.Token;
        token.Register(() =>
        {
            // Two distinct re-arm scenarios — both checked separately so the
            // guard is explicit (not a single AND that conflates them):
            //
            //   1. FF→FF interruption: CancelReceiveWatchdog (called by
            //      HandleFirstFrame before installing the new FF's watchdog)
            //      sets _rxWatchdog = null under _rxLock BEFORE calling
            //      old.Cts.Cancel(). The OLD FF's Register callback (queued
            //      by the cancellation propagation) then takes _rxLock and
            //      finds _rxWatchdog == null: short-circuit without touching
            //      _rxInProgress / _rxBuffer — the NEW FF owns them.
            //      BOTH FFs share Generation=1 (FF always re-arms at gen 1)
            //      so the Generation check below is a no-op here; the null
            //      guard is the only protection.
            //
            //   2. CF→CF re-arm: HandleConsecutiveFrame re-arms the watchdog
            //      with a NEW generation (sourced from _rxExpectedSequence,
            //      which advances on every CF boundary). The OLD CF's
            //      callback then sees _rxWatchdog.Generation != expectedGen
            //      and short-circuits without disturbing the NEW CF.
            lock (_rxLock)
            {
                if (_rxWatchdog is null)
                    return; // FF→FF: we were superseded; new FF owns _rxLock state
                if (_rxWatchdog.Generation != expectedGeneration)
                    return; // CF→CF: generation advanced past us
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