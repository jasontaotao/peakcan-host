using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>
/// Pure ASC/BLF trace analysis. Groups frames by raw CAN ID, derives stable
/// periodic intervals, and exposes J1939 metadata so UI can filter DUT sources.
/// </summary>
public static class TraceRestbusRecognizer
{
    public static TraceRecognitionResult Recognize(
        IReadOnlyList<ReplayFrame> frames,
        TraceRecognitionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var resolved = options ?? new TraceRecognitionOptions();

        var groups = frames
            .Where(f => (f.Flags & FrameFlags.ErrFrame) == 0)
            .GroupBy(f => (f.Channel, f.Id, f.IsExtended))
            .OrderBy(g => g.Key.Channel)
            .ThenBy(g => g.Key.Id)
            .ThenBy(g => g.Key.IsExtended);

        var candidates = new List<TraceFrameCandidate>();
        foreach (var group in groups)
        {
            var sorted = group.OrderBy(f => f.Timestamp).ToArray();
            if (sorted.Length == 0)
                continue;

            var first = sorted[0];
            if (resolved.ExcludedIds is { } excludedIds && excludedIds.Contains(first.Id))
                continue;

            J1939Id j1939 = default;
            byte? source = null;
            byte? destination = null;
            uint? priority = null;
            uint? pgn = null;
            if (first.IsExtended)
            {
                j1939 = new J1939Id(first.Id & J1939Id.Raw29Mask);
                source = j1939.SourceAddress;
                destination = j1939.DestinationAddress;
                priority = j1939.Priority;
                pgn = j1939.Pgn;

                if (resolved.ExcludedJ1939SourceAddresses is { } excludedSources &&
                    excludedSources.Contains(j1939.SourceAddress))
                {
                    continue;
                }
            }

            var deltas = new double[sorted.Length - 1];
            for (var i = 1; i < sorted.Length; i++)
                deltas[i - 1] = Math.Max(0, (sorted[i].Timestamp - sorted[i - 1].Timestamp) * 1000.0);

            var median = Median(deltas);
            var mad = deltas.Length == 0 ? 0 : Median(deltas.Select(d => Math.Abs(d - median)).ToArray());
            var cv = median <= 0 ? 0 : mad / median;
            var intervalMs = Math.Max(10, (int)Math.Round(median));
            var isPeriodic =
                deltas.Length >= Math.Max(1, resolved.MinFrames) &&
                median >= 10 &&
                cv <= resolved.MaxIntervalCv;

            candidates.Add(new TraceFrameCandidate(
                first.Channel,
                first.Id,
                first.IsExtended,
                sorted.Length,
                intervalMs,
                cv,
                isPeriodic,
                sorted.Any(f => (f.Flags & FrameFlags.Fd) != 0),
                source,
                destination,
                priority,
                pgn,
                sorted[^1].Data));
        }

        return new TraceRecognitionResult(candidates, []);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
            return 0;

        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
