using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming ASC source: opens the stream, eagerly reads header lines
/// (<c>date</c>/<c>base</c>) up to the first data line, then lazily yields
/// <see cref="ReplayFrame"/>s line-by-line — never materializing the whole
/// file. Supports binary-search seek: when <paramref name="skipUntil"/> is
/// provided and the stream is seekable, O(log n) probes locate the first
/// frame ≥ target, making seek near-instant regardless of file size.
/// </summary>
public sealed class AscStreamingSource : IStreamingTraceSource
{
    private readonly Func<Stream> _streamFactory;
    private readonly ILogger _logger;

    public AscStreamingSource(Func<Stream> streamFactory, ILogger? logger = null)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default)
    {
        Stream? stream = null;
        try
        {
            stream = _streamFactory();
            long? length = stream.CanSeek ? stream.Length : null;
            var stats = new StreamingParseStats();
            var reader = new StreamReader(stream, leaveOpen: true);

            DateTime? origin = null;
            bool absolute = false;
            string? firstDataLine = null;

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                AddBytes(stats, line);
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (t.StartsWith("date ", StringComparison.Ordinal)) { origin ??= AscFormat.TryParseDateHeader(t); continue; }
                if (t.StartsWith("base ", StringComparison.Ordinal)) { absolute = t.Contains("absolute", StringComparison.OrdinalIgnoreCase); continue; }
                if (t.StartsWith("internal events", StringComparison.Ordinal)) continue;
                if (AscFormat.LineIsSectionDelimiter(t)) continue;
                firstDataLine = t;
                break;
            }

            // 二分 seek：O(log n) 次探测定位，替代逐行线性跳过
            if (skipUntil.HasValue && stream.CanSeek && firstDataLine is not null)
            {
                if (ShouldSkip(firstDataLine, skipUntil))
                {
                    var targetOffset = await BinarySeekAsync(stream, skipUntil.Value, ct).ConfigureAwait(false);
                    if (targetOffset.HasValue)
                    {
                        reader.Dispose();
                        stream.Seek(targetOffset.Value, SeekOrigin.Begin);
                        reader = new StreamReader(stream, leaveOpen: true);
                        firstDataLine = null;
                    }
                }
                skipUntil = null; // 二分后或首帧已达标，不再线性过滤
            }

            var frames = Enumerate(reader, firstDataLine, stats, skipUntil, ct);
            return new StreamingTraceOpenResult
            {
                WallClockOrigin = origin,
                TimestampsAreAbsolute = absolute,
                Frames = frames,
                SourceLengthBytes = length,
                Stats = stats,
                SourceStream = stream,
            };
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Binary search the stream for the byte offset of the first data line
    /// with timestamp ≥ targetTs. ASC timestamps are monotonic → O(log n) probes.
    /// Each probe reads ≤ ProbeBufferSize bytes from a seeked position.
    /// </summary>
    private const int ProbeBufferSize = 512;

    private static async Task<long?> BinarySeekAsync(Stream stream, double targetTs, CancellationToken ct)
    {
        long lo = 0, hi = stream.Length;
        long best = -1;
        var buffer = new byte[ProbeBufferSize];

        int iterations = 0;
        while (lo < hi && ++iterations < 60)
        {
            long mid = lo + (hi - lo) / 2;
            var probe = await ProbeTimestampAsync(stream, mid, buffer, ct).ConfigureAwait(false);
            if (probe is null)
            {
                hi = mid; // Can't parse here → search left
                continue;
            }
            var (offset, ts) = probe.Value;
            if (ts >= targetTs)
            {
                best = offset;
                hi = Math.Min(offset, hi); // Earlier match may exist to the left
            }
            else
            {
                lo = mid + 1;
            }
        }
        return best > 0 ? best : null;
    }

    /// <summary>
    /// Seek to <paramref name="from"/>, skip to next line boundary, read the
    /// complete line, and parse its timestamp. Returns the line's byte offset.
    /// </summary>
    private static async Task<(long offset, double ts)?> ProbeTimestampAsync(
        Stream stream, long from, byte[] buffer, CancellationToken ct)
    {
        if (from >= stream.Length) return null;
        stream.Seek(from, SeekOrigin.Begin);
        var bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (bytesRead == 0) return null;

        // Skip partial line: find first newline
        var nl = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
        if (nl < 0 || nl + 1 >= bytesRead) return null;
        var lineStart = nl + 1;

        // Handle \r\n
        if (lineStart < bytesRead && buffer[lineStart] == (byte)'\r')
        {
            lineStart++;
            if (lineStart >= bytesRead) return null;
        }

        // Find end of the complete line
        var lineEnd = Array.IndexOf(buffer, (byte)'\n', lineStart, bytesRead - lineStart);
        if (lineEnd < 0) lineEnd = bytesRead;
        // Strip trailing \r
        if (lineEnd > lineStart && buffer[lineEnd - 1] == (byte)'\r') lineEnd--;

        var len = lineEnd - lineStart;
        if (len <= 0) return null;

        var text = System.Text.Encoding.UTF8.GetString(buffer, lineStart, len);
        var trimmed = text.AsSpan().Trim();
        var sp = trimmed.IndexOf(' ');
        if (sp <= 0) return null;
        if (!double.TryParse(trimmed.Slice(0, sp), NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
            return null;

        return (from + lineStart, ts);
    }

    private async IAsyncEnumerable<ReplayFrame> Enumerate(
        StreamReader reader,
        string? firstDataLine,
        StreamingParseStats stats,
        double? skipUntil,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long emitted = 0, malformed = 0, dataLines = 0;
        try
        {
            if (firstDataLine is not null)
            {
                dataLines++;
                if (skipUntil.HasValue && ShouldSkip(firstDataLine, skipUntil))
                {
                    // 跳过首行（未达目标时间戳）
                }
                else if (AscFormat.TryParseDataLine(firstDataLine, out var f0, out _))
                {
                    emitted++;
                    yield return f0;
                }
                else
                {
                    malformed++;
                    stats.IncSkipped();
                }
            }

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                AddBytes(stats, line);
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (t.StartsWith("date ", StringComparison.Ordinal)) continue;
                if (t.StartsWith("base ", StringComparison.Ordinal)) continue;
                if (t.StartsWith("internal events", StringComparison.Ordinal)) continue;
                if (AscFormat.LineIsSectionDelimiter(t)) continue;

                dataLines++;
                // skipUntil fast-path: only extract timestamp (no binary seek case)
                if (skipUntil.HasValue)
                {
                    var sp = t.IndexOf(' ');
                    if (sp > 0 && double.TryParse(t.AsSpan(0, sp), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var ts) && ts < skipUntil.Value)
                    {
                        continue;
                    }
                    skipUntil = null;
                }
                if (AscFormat.TryParseDataLine(t, out var frame, out var reason))
                {
                    emitted++;
                    yield return frame;
                }
                else
                {
                    malformed++;
                    stats.IncSkipped();
                    _logger.LogDebug("Skipped malformed ASC line: {Reason}", reason);
                }
            }
        }
        finally
        {
            reader.Dispose();
        }

        if (emitted == 0)
            throw new ReplayFormatException(
                $"ASC file has no parseable frames (saw {dataLines} data lines, all malformed).");
        if (dataLines > 0 && (double)malformed / dataLines > 0.5)
            throw new ReplayFormatException(
                $"ASC file appears corrupted ({malformed}/{dataLines} = {100.0 * malformed / dataLines:F0}% malformed).");
    }

    /// <summary>轻量时间戳检查：首个 token 是合法 double 且 &lt; skipUntil 则返回 true。</summary>
    private static bool ShouldSkip(string line, double? skipUntil)
    {
        if (!skipUntil.HasValue) return false;
        var sp = line.IndexOf(' ');
        if (sp <= 0) return false;
        return double.TryParse(line.AsSpan(0, sp), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var ts) && ts < skipUntil.Value;
    }

    private static void AddBytes(StreamingParseStats stats, string line)
    {
        stats.AddBytes(line.Length + 1);
    }
}

