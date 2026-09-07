using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming ASC source: opens the stream, eagerly reads header lines
/// (<c>date</c>/<c>base</c>) up to the first data line, then lazily yields
/// <see cref="ReplayFrame"/>s line-by-line — never materializing the whole
/// file. <c>date</c>/<c>base</c> lines appearing AFTER the first data line
/// are ignored (batch parser catches them anywhere; pathological for real
/// files — documented divergence). Malformed lines are skipped and counted
/// in <see cref="StreamingParseStats.SkippedLines"/>. The ">50% malformed"
/// and "no parseable frames" guards throw <see cref="ReplayFormatException"/>
/// at enumeration end (timing-equivalent to batch, which also reads all
/// lines before throwing).
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

    public async Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default)
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
                firstDataLine = t; // 第一个候选数据行（也可能畸形）
                break;
            }

            var frames = Enumerate(reader, firstDataLine, stats, ct);
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

    private async IAsyncEnumerable<ReplayFrame> Enumerate(
        StreamReader reader,
        string? firstDataLine,
        StreamingParseStats stats,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long emitted = 0, malformed = 0, dataLines = 0;
        try
        {
            // 先处理 OpenAsync 已读到的首个候选数据行
            if (firstDataLine is not null)
            {
                dataLines++;
                if (AscFormat.TryParseDataLine(firstDataLine, out var f0, out _))
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

        // 与批量解析器等价的尾部校验
        if (emitted == 0)
            throw new ReplayFormatException(
                $"ASC file has no parseable frames (saw {dataLines} data lines, all malformed).");
        if (dataLines > 0 && (double)malformed / dataLines > 0.5)
            throw new ReplayFormatException(
                $"ASC file appears corrupted ({malformed}/{dataLines} = {100.0 * malformed / dataLines:F0}% malformed).");
    }

    private static void AddBytes(StreamingParseStats stats, string line)
    {
        // UTF-8 字节近似：line.Length 是 UTF-16 char 数；多数 ASC 为 ASCII → 1B/char。
        stats.AddBytes(line.Length + 1);
    }
}
