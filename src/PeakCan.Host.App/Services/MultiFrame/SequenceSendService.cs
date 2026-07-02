using PeakCan.Host.App.Services;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.MultiFrame;

/// <summary>
/// v2.1.0 MINOR: send a sequence of CAN frames in either
/// concurrent (all at once via <see cref="Task.WhenAll"/>) or
/// sequential (one after another with optional inter-frame delay)
/// mode. Iteration count repeats the whole sequence N times.
///
/// <para>
/// Lives in the App assembly (not Core) because it depends on
/// <see cref="SendService"/>, an App-layer type that wraps the
/// raw channel send with rate-limiting + Result envelopes.
/// </para>
///
/// <para>
/// Cancellation: <see cref="SendAsync"/> honors the supplied
/// <see cref="CancellationToken"/> between iterations and between
/// sequential frames. Concurrent mode cancels all in-flight
/// sub-tasks on first cancel.
/// </para>
/// </summary>
public sealed class SequenceSendService
{
    private readonly SendService _sendService;

    public SequenceSendService(SendService sendService)
    {
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
    }

    /// <summary>Send mode: concurrent vs sequential.</summary>
    public enum Mode
    {
        /// <summary>Fire all frames in the iteration concurrently via Task.WhenAll.</summary>
        Concurrent,
        /// <summary>Send frames one after another in order, with optional DelayMs between each.</summary>
        Sequential,
    }

    /// <summary>Outcome of one sequence send operation.</summary>
    public sealed record Result(int SentCount, int FailureCount, int IterationsCompleted)
    {
        public bool AllSucceeded => FailureCount == 0;
    }

    /// <summary>
    /// Send the frames in <paramref name="frames"/> repeated
    /// <paramref name="iterations"/> times using the chosen
    /// <paramref name="mode"/>. <paramref name="delayMs"/> only
    /// applies to sequential mode (delay between consecutive frames;
    /// 0 = fire immediately).
    /// </summary>
    public async Task<Result> SendAsync(
        IReadOnlyList<CanFrame> frames,
        Mode mode,
        int delayMs,
        int iterations,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
        if (frames.Count == 0)
            return new Result(SentCount: 0, FailureCount: 0, IterationsCompleted: 0);

        var sent = 0L;
        var failed = 0L;
        var iterationsCompleted = 0;

        for (var it = 0; it < iterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            if (mode == Mode.Concurrent)
            {
                var tasks = new Task<bool>[frames.Count];
                for (var i = 0; i < frames.Count; i++)
                {
                    var f = frames[i];
                    tasks[i] = SendOneAsync(f, ct);
                }
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                for (var i = 0; i < results.Length; i++)
                {
                    if (results[i]) Interlocked.Increment(ref sent);
                    else            Interlocked.Increment(ref failed);
                    progress?.Report((int)Interlocked.Read(ref sent) + (int)Interlocked.Read(ref failed));
                }
            }
            else // Sequential
            {
                for (var i = 0; i < frames.Count; i++)
                {
                    if (i > 0 && delayMs > 0)
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var ok = await SendOneAsync(frames[i], ct).ConfigureAwait(false);
                    if (ok) Interlocked.Increment(ref sent);
                    else    Interlocked.Increment(ref failed);
                    progress?.Report((int)Interlocked.Read(ref sent) + (int)Interlocked.Read(ref failed));
                }
            }
            iterationsCompleted++;
        }

        return new Result(
            SentCount: (int)Interlocked.Read(ref sent),
            FailureCount: (int)Interlocked.Read(ref failed),
            IterationsCompleted: iterationsCompleted);
    }

    private async Task<bool> SendOneAsync(CanFrame frame, CancellationToken ct)
    {
        try
        {
            // CA2016: forward the supplied token so the underlying
            // SendService observes cancellation when the caller stops
            // the sequence. Pre-CA2016 fix we omitted it on purpose
            // ("fire-and-forget") but that masks cancellation latency.
            var r = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
            return r.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}