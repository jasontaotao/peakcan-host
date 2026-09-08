using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Scans a BLF stream through <see cref="BlfStreamingSource"/> to obtain total
/// duration and frame count without retaining parsed frames. Progress is
/// based on parser bytes consumed.
/// </summary>
public static class BlfDurationScanner
{
    public static async Task<DurationScanResult> ScanAsync(
        Stream stream,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        long total = stream.CanSeek ? stream.Length : 0;
        long count = 0;
        double first = double.NaN, last = 0;

        var source = new BlfStreamingSource(() => stream);
        await using var open = await source.OpenAsync(ct: ct).ConfigureAwait(false);
        await foreach (var frame in open.Frames.WithCancellation(ct).ConfigureAwait(false))
        {
            if (count == 0) first = frame.Timestamp;
            last = frame.Timestamp;
            count++;
            if (progress is not null && total > 0 && (count & 0xFFF) == 0)
                progress.Report(Math.Clamp((double)open.Stats.BytesRead / total, 0, 1));
        }

        progress?.Report(1.0);
        double duration = count > 0 && !double.IsNaN(first) ? last - first : 0;
        return new DurationScanResult(duration, count, open.WallClockOrigin);
    }
}
