// CyclicSendService/Lifecycle.partial.cs — W34 T1 (Flow A, 89 LoC)
// Lifecycle methods: Start (Start timer + reset counters) + Stop + StopInner
// (private helper) + Dispose (cleanup CTS). All 4 touch _timer + _cts +
// _isRunning state. Sister of W22 RecordService Lifecycle + W23
// CyclicDbcSendService TickLifecycle + W27 RecentSessionsService
// PersistenceOps + W28 DbcService LoadLifecycle + W29 SendFrameLibrary
// PersistenceFlow + W30 SequenceSendService SendFlow + W31 ReplayService
// FileIoLifecycle + W32 DbcApi LoadFlow + W33 SequenceLibrary
// PersistenceFlow file-IO/state-management sister-pattern.
//
// Cross-partial caller pattern: Start (in Lifecycle partial) creates _timer
// via _timerFactory.CreateCyclicTimer(OnTimerTick, ...) — OnTimerTick (in
// TimerTick partial) passed as delegate. Partial-class cross-partial
// visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27
// +W28+W29+W30+W31+W32+W33 cross-partial helper pattern).
//
// W23 STRUCT-FABRICATION LESSON: Interlocked.Exchange 2-arg +
// Interlocked.Read 1-arg + CancellationTokenSource 0-arg + lock(this)
// statement signatures verified during verbatim re-extraction.
//
// 4 [LoggerMessage] declarations (LogCyclicStarted + LogCyclicStopped +
// LogCyclicSendFailed + LogCyclicSendThrew) stay on CyclicSendService.cs
// per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent
// (CS8795 mitigation). Called from Start (this partial) + OnTimerTick (in
// TimerTick partial).
//
// W34 T1 verbatim re-extracted via `git show main:src/.../CyclicSendService.cs | sed -n '95,131p;133,140p;141,157p;218,230p'`
// per W20 T2 R1 fabrication LESSON (44th application).

using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicSendService
{
    /// <summary>
    /// Start cyclic transmission of <paramref name="frame"/> at
    /// <paramref name="interval"/>. If already running, stops first.
    /// </summary>
    public void Start(CanFrame frame, TimeSpan interval)
    {
        long gen;
        lock (this)
        {
            // StopInner under the same lock so an in-flight OnTimerTick
            // observes the _isRunning flip atomically with our _frame /
            // _generation updates.
            StopInner();
            _frame = frame;
            _interval = interval;
            _isRunning = true;
            // v1.2.12 PATCH Item 10 (Review Cycle 1 I-1): reset the split
            // counters so "since the last Start" remains the documented
            // contract. Without this, a second Start would carry counts
            // from the previous cycle.
            Interlocked.Exchange(ref _sendSuccessCount, 0);
            Interlocked.Exchange(ref _sendFailureCount, 0);
            // v1.6.2 PATCH Item 1a: dispose previous CTS (if any) and create
            // a fresh one. Without this, a second Start would inherit a
            // cancelled token and every tick would throw OCE on SendAsync.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            gen = ++_generation;
            // v3.5.4 PATCH: timer obtained from the factory. The fake
            // (FakeCyclicTimer) records the callback + state + period
            // but does not auto-fire; tests call Fire() to advance
            // deterministically.
            _timer = _timerFactory.CreateCyclicTimer(OnTimerTick, gen, interval);
        }
        LogCyclicStarted(_logger, frame.Id, interval.TotalMilliseconds);
    }

    /// <summary>Stop cyclic transmission. Idempotent.</summary>
    public void Stop()
    {
        lock (this)
        {
            StopInner();
        }
    }

    private void StopInner()
    {
        if (!_isRunning) return;
        _isRunning = false;
        // Bump generation so any in-flight OnTimerTick that already passed
        // the lock check observes a mismatch and bails before SendAsync.
        _generation++;
        _timer?.Dispose();
        _timer = null;
        // v1.6.2 PATCH Item 1a: cancel in-flight SendAsync. The CTS was
        // snapshotted by OnTimerTick under the lock above; cancelling here
        // propagates through _sendService.SendAsync(frame, ct) into
        // ch.WriteAsync(frame, ct) which honors the token.
        _cts?.Cancel();
        LogCyclicStopped(_logger, SuccessCount);
    }

    public void Dispose()
    {
        lock (this)
        {
            StopInner();
            // v1.6.2 PATCH Item 1a: dispose the CTS to release its internal
            // ManualResetEvent handle. Without this, repeated Start/Stop
            // cycles leak native resources.
            _cts?.Dispose();
            _cts = null;
        }
    }
}
