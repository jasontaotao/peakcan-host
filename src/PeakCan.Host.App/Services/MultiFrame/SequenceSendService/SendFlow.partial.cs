// SequenceSendService/SendFlow.partial.cs — W30 T1 (Flow A, LARGEST 91 LoC)
// SendAsync orchestration: dispatches the row sequence in concurrent
// (Task.WhenAll fan-out) or sequential (Task.Delay loop) mode for the
// requested iteration count. Per-row build errors count as row-level
// failures and continue with the remaining rows (v2.1.1 PATCH semantics).
//
// W25 D5 deviation APPLIED: SendAsync 91 LoC LARGEST method MOVES per the
// sharp discrete flow boundary criterion (concurrent vs sequential
// dispatcher = 2 distinct dispatching paths, NOT a single central
// orchestration loop). Sister of W25 OnChannelFrame 73 LoC + W26 OnFrame
// 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC moves.
//
// Cross-partial helper pattern (W22+W23+W24+W25+W26+W27+W28+W29 sister):
// TryBuildRow + SendOneAsync private helpers live in RowBuildFlow.partial.cs
// and are called from this partial via partial-class visibility.
//
// W23 STRUCT-FABRICATION LESSON: CanId(raw, FrameFormat format) 2-arg +
// CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)
// 5-arg signatures verified during verbatim re-extraction from HEAD.
//
// W30 T1 verbatim re-extracted via `git show HEAD:src/.../SequenceSendService.cs | sed -n '75,165p'`
// per W20 T2 R1 fabrication LESSON (35th application).

using PeakCan.Host.App.Models;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.MultiFrame;

public sealed partial class SequenceSendService
{
    /// <summary>
    /// Send the rows in <paramref name="rows"/> repeated
    /// <paramref name="iterations"/> times using the chosen
    /// <paramref name="mode"/>.
    /// </summary>
    public async Task<Result> SendAsync(
        IReadOnlyList<MultiFrameSequenceRow> rows,
        Mode mode,
        int delayMs,
        int iterations,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
        if (rows.Count == 0)
            return new Result(SentCount: 0, FailureCount: 0, IterationsCompleted: 0);

        var sent = 0L;
        var failed = 0L;
        var iterationsCompleted = 0;

        for (var it = 0; it < iterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            // Build all frames for this iteration up front so a
            // misconfigured row (bad hex, missing DBC message) fails
            // BEFORE any send in this iteration goes on the wire.
            // Per-row build errors count as a failure of that row.
            var frames = new List<CanFrame>(rows.Count);
            var rowFailures = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (TryBuildRow(row, out var frame, out var buildError))
                {
                    frames.Add(frame);
                }
                else
                {
                    // v2.1.1 PATCH: a build failure (e.g. unknown DBC
                    // message) counts as a per-row failure but doesn't
                    // abort the whole sequence — continue with the
                    // remaining rows.
                    rowFailures++;
                }
            }

            if (frames.Count == 0)
            {
                failed += rowFailures;
                progress?.Report((int)Interlocked.Read(ref sent) + (int)Interlocked.Read(ref failed));
                iterationsCompleted++;
                continue;
            }

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
                }
                Interlocked.Add(ref failed, rowFailures);
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
                }
                Interlocked.Add(ref failed, rowFailures);
            }

            progress?.Report((int)Interlocked.Read(ref sent) + (int)Interlocked.Read(ref failed));
            iterationsCompleted++;
        }

        return new Result(
            SentCount: (int)Interlocked.Read(ref sent),
            FailureCount: (int)Interlocked.Read(ref failed),
            IterationsCompleted: iterationsCompleted);
    }
}
