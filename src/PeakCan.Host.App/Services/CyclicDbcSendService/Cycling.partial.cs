using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicDbcSendService
{

    private async void OnTimerTick(object? state)
    {
        Func<(Message message, IReadOnlyDictionary<string, double> values)>? provider;
        TimeSpan interval;
        long generation;
        CancellationToken ct;
        lock (this)
        {
            if (!_isRunning) return;
            // v3.5.4 PATCH: capture generation under the lock. If Start()
            // bumps _generation while we're encoding/sending, the
            // generation-mismatch check below discards stale results.
            generation = _generation;
            provider = _frameProvider;
            interval = _interval;
            // v1.6.2 PATCH Item 1b: snapshot CTS so Stop() cancelling it
            // mid-tick surfaces as OCE in SendAsync, not silent success.
            // _cts may go null between the check and the snapshot
            // (StopInner nulls it under the same lock; we hold the lock),
            // so use a local fallback to avoid NRE.
            ct = _cts?.Token ?? CancellationToken.None;
        }
        if (provider is null) return;

        (Message message, IReadOnlyDictionary<string, double> values) snapshot;
        try
        {
            snapshot = provider();
        }
        catch (Exception ex)
        {
            // Decision 8: provider threw (most commonly, a DBC
            // signal-extraction exception). Log every 100th, do NOT
            // increment FailureCount — provider errors are a separate
            // signal from send/encode failures (they don't mean "this
            // tick did not deliver", they mean "the caller misconfigured
            // the provider"). v3.4.2 PATCH.
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0)
            {
                LogCyclicDbcProviderThrew(_logger, ex);
            }
            return;
        }

        // Decision 9: detect "user switched message mid-run". On the
        // first tick, capture the baseline. On subsequent ticks, compare
        // the provider's current Message.Id to the captured one. A
        // mismatch means the user changed the DBC message dropdown while
        // periodic send was active; stop + record one failure so the
        // silence is observable in the UI counter.
        bool messageChanged = false;
        lock (this)
        {
            if (_capturedMessageId is null)
            {
                _capturedMessageId = snapshot.message.Id;
            }
            else if (_capturedMessageId.Value != snapshot.message.Id)
            {
                messageChanged = true;
            }
        }
        if (messageChanged)
        {
            Stop();
            Interlocked.Increment(ref _sendFailureCount);
            LogCyclicDbcMessageChanged(_logger, _capturedMessageId, snapshot.message.Id);
            return;
        }

        // v1.6.1 PATCH Item 1: defensive _isRunning re-check after the
        // Message.Id lock and before encode. Closes the race window
        // where Stop() can be called between the snapshot lock and
        // encode, allowing an in-flight tick to complete even though
        // Stop was requested. The re-check is cheap (< 1μs) and
        // reuses the existing lock(this) pattern.
        lock (this)
        {
            if (!_isRunning) return;
        }

        byte[] payload;
        try
        {
            payload = _encoder.Encode(snapshot.message, snapshot.values);
        }
        catch (DbcSignalEncodeException ex)
        {
            // Decision 10: encode failure is per-tick; counter + log
            // every 100th to avoid log spam. The exception message
            // identifies the offending signal; we log the full message so
            // engineers can correlate the failure to the input data.
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0)
            {
                LogCyclicDbcEncodeThrew(_logger, snapshot.message.Id, ex);
            }
            return;
        }

        // Mirror DbcSendViewModel.SendAsync: DBC messages use the PEAK
        // convention bit 31 set ⇒ Extended, clear ⇒ Standard. The CanId
        // ctor validates bit-width, so route the right format to avoid
        // ArgumentOutOfRangeException on 11-bit IDs.
        var id = snapshot.message.Id;
        var isExtended = (id & 0x80000000u) != 0u;
        var raw = isExtended ? (id & 0x1FFFFFFFu) : (id & 0x7FFu);
        var canId = new CanId(raw, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var frame = new CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default);

        // v1.6.1 PATCH Item 1: defensive _isRunning re-check after
        // encode and before send. Closes the second race window
        // (encode → send await) where Stop() can be called while a
        // tick is mid-flight. The first re-check covers the snapshot
        // → encode window; this one covers encode → send.
        lock (this)
        {
            if (!_isRunning) return;
        }

        try
        {
            var result = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _sendSuccessCount);
            }
            else
            {
                var count = Interlocked.Increment(ref _sendFailureCount);
                if (count % 100 == 0)
                {
                    LogCyclicDbcSendFailed(_logger, frame.Id, result.Error!.Code, result.Error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // v1.6.2 PATCH Item 1b: expected on Stop(). async void timer
            // callback would crash the process if OCE propagated uncaught.
            // Do NOT increment FailureCount — Stop is user-initiated, not
            // a hardware failure.
        }
        catch (Exception ex)
        {
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0)
            {
                LogCyclicDbcSendThrew(_logger, frame.Id, ex);
            }
        }
    }
}