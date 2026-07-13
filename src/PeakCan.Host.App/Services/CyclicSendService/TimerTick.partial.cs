// CyclicSendService/TimerTick.partial.cs — W34 T2 (Flow B, 61 LoC)
// Private async timer-tick handler: OnTimerTick dispatches the cyclic
// frame send on each timer tick. Sister of W23 CyclicDbcSendService.
// OnTimerTick (151 LoC STAYED INLINE per W22+W23 orchestration-loop stay pattern).
//
// W25 D5 deviation NOT applied: OnTimerTick 61 LoC LARGEST method
// ≥ 60 LoC threshold BUT orchestration-loop shape (timer fires → state-machine
// dispatch loop) → fails W25 D5 deviation criteria #3 → STAYS INLINE
// per W22+W23 sister precedent.
//
// Cross-partial caller pattern: Lifecycle partial creates _timer via
// _timerFactory.CreateCyclicTimer(OnTimerTick, ...). OnTimerTick (in
// this partial) reads _frame + _sendService + _generation + _cts + _logger
// + _sendSuccessCount + _sendFailureCount. Partial-class cross-partial
// visibility handles this automatically.
//
// 4 [LoggerMessage] declarations stay on CyclicSendService.cs (CS8795
// mitigation per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34
// sister precedent). Called from OnTimerTick (this partial).
//
// W23 STRUCT-FABRACTION LESSON: Interlocked.Increment(ref long) 1-arg
// + SendAsync(CanFrame, CancellationToken) 2-arg signatures verified.
//
// W34 T2 verbatim re-extracted via `git show main:src/.../CyclicSendService.cs | sed -n '158,218p'`
// per W20 T2 R1 fabrication LESSON (45th application).

using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicSendService
{
    private async void OnTimerTick(object? state)
    {
        CanFrame frame;
        TimeSpan interval;
        long generation;
        CancellationToken ct;
        lock (this)
        {
            if (!_isRunning) return;
            frame = _frame;
            interval = _interval;
            generation = _generation;
            // v1.6.2 PATCH Item 1a: snapshot CTS.Token under the same lock
            // as _isRunning + _frame + _generation so the tick sees a
            // coherent view. If Start replaced _cts after the timer fired
            // but before we acquired the lock, the snapshot reflects the
            // current CTS — Stop on the new CTS will cancel this tick.
            ct = _cts?.Token ?? CancellationToken.None;
        }
        // Stale-timer drop: if this Timer was disposed (Start re-entered)
        // its captured generation no longer matches the service's. Bail
        // before touching SendAsync.
        if (state is long tickGen && tickGen != generation) return;

        try
        {
            // v1.6.2 PATCH Item 1a: pass CT so Stop() can abort the in-flight
            // channel write. _sendService.SendAsync forwards ct to
            // ch.WriteAsync(frame, ct) which honors the token.
            var result = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _sendSuccessCount);
            }
            else
            {
                // Don't spam logs — only log every 100th failure.
                var count = Interlocked.Increment(ref _sendFailureCount);
                if (count % 100 == 0 && _logger is not null)
                {
                    LogCyclicSendFailed(_logger, frame.Id, result.Error!.Code, result.Error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // v1.6.2 PATCH Item 1a: expected on Stop(). async void timer
            // callback would crash the process if OCE propagated uncaught.
            // Do NOT increment FailureCount — Stop is user-initiated, not
            // a hardware failure.
        }
        catch (Exception ex)
        {
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0 && _logger is not null)
            {
                LogCyclicSendThrew(_logger, frame.Id, ex);
            }
        }
    }
}
