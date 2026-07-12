using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Parses Vector ASCII (ASC) trace files into <see cref="ReplayFrame"/>
/// records. Tolerant of headers (`date`, `base`, `internal events`),
/// comments (`//`-prefixed), empty lines, and trailing footer.
/// Malformed data lines are logged and skipped (not thrown).
/// </summary>
public static partial class AscParser
{
    /// <summary>
    /// Active logger. Defaults to <see cref="NullLogger.Instance"/>; can be
    /// overridden via <see cref="ParseAsync(Stream, ILogger{AscParser}?, CancellationToken)"/>.
    /// <para>
    /// We use the non-generic <see cref="NullLogger"/> rather than
    /// <c>NullLogger&lt;AscParser&gt;</c> because C# forbids using a static
    /// class as a generic type argument. The log category name is therefore
    /// not propagated to the entry, but the message structure (line number,
    /// raw line, reason) carries enough context for production debugging.
    /// </para>
    /// </summary>
    private static ILogger _logger = NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Warning,
                   Message = "Skipped malformed ASC line {LineNumber}: {RawLine} ({Reason})")]
    private static partial void LogSkippedLine(ILogger logger, int lineNumber, string rawLine, string reason);

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): parse <paramref name="stream"/> as ASC with a
    /// hard stream-size cap. Seekable streams are pre-checked against
    /// <see cref="ReplayOptions.MaxFileSizeBytes"/> via <c>stream.Length</c>
    /// (cheap stat, no walk); non-seekable streams are wrapped in a
    /// <see cref="CountingStream"/> that throws <see cref="ReplayLoadException"/>
    /// the moment cumulative bytes read exceed the cap.
    /// </summary>
    /// <param name="stream">Source stream. May be seekable or non-seekable.</param>
    /// <param name="options">Replay-layer cap + future knobs. Must be non-null.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger"/>.</param>
    /// <exception cref="ReplayLoadException">
    /// Thrown when the stream exceeds <see cref="ReplayOptions.MaxFileSizeBytes"/>,
    /// or on IO error reading the stream.
    /// </exception>
    /// <exception cref="ReplayFormatException">
    /// Thrown when the file has no parseable frames or >50% of data lines are malformed.
    /// </exception>
    public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        ReplayOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger.Instance;

        // Cheap stat-based precheck for seekable streams (files on disk,
        // MemoryStream). A multi-GB ASC fails here without entering the
        // read loop. Mirrors TraceViewerService.LoadAsync's FileInfo.Length
        // precheck (defense-in-depth at the parser layer).
        if (stream.CanSeek && stream.Length > options.MaxFileSizeBytes)
        {
            throw new ReplayLoadException(
                $"ASC stream exceeds size cap ({stream.Length:N0} > {options.MaxFileSizeBytes:N0} bytes)");
        }

        // For non-seekable streams (pipes, network, wrapped streams)
        // we cannot stat the total length, so wrap with a counting
        // stream that throws once cumulative bytes read exceed the cap.
        Stream effective = stream.CanSeek
            ? stream
            : new CountingStream(stream, options.MaxFileSizeBytes);

        // Read all lines
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(effective, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            throw new ReplayLoadException("Failed to read ASC stream", ex);
        }

        var (frames, _, _) = ParseLines(lines);
        return frames;
    }

    /// <summary>
    /// Backward-compatible overload preserved for callers that pre-date
    /// the v3.10.0 MINOR T4 (H5) size-cap addition. Delegates to the
    /// <see cref="ReplayOptions"/> overload with
    /// <see cref="ReplayOptions.Default"/> (200 MB cap).
    /// </summary>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger"/>.</param>
    public static Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        return ParseAsync(stream, ReplayOptions.Default, logger, ct);
    }

    /// <summary>
    /// Backward-compatible overload for v1.4.0 callers that pre-date the
    /// v1.4.1 PATCH Item 2 logging addition (e.g.
    /// <c>ReplayService.LoadAsync</c>). Delegates to the logging-aware
    /// overload with <see cref="NullLogger.Instance"/>.
    /// </summary>
    public static Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        CancellationToken ct)
    {
        return ParseAsync(stream, logger: null, ct);
    }

    /// <summary>
    /// v3.18.0 PATCH (Trace Viewer Enhancements): parses the ASC stream
    /// and returns both the frame list AND the header metadata
    /// (wall-clock origin from the <c>date</c> line, timestamp mode from
    /// the <c>base hex  timestamps ...</c> line). Use this overload when
    /// the X-axis needs to render wall-clock labels; otherwise prefer
    /// the existing <see cref="ParseAsync(Stream, ReplayOptions, ILogger, CancellationToken)"/>
    /// overload for unchanged call-site behavior.
    /// </summary>
    public static async Task<AscParseResult> ParseAsyncWithHeaderAsync(
        Stream stream,
        ReplayOptions? options = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        options ??= ReplayOptions.Default;
        _logger = logger ?? NullLogger.Instance;

        if (stream.CanSeek && stream.Length > options.MaxFileSizeBytes)
        {
            throw new ReplayLoadException(
                $"ASC stream exceeds size cap ({stream.Length:N0} > {options.MaxFileSizeBytes:N0} bytes)");
        }

        Stream effective = stream.CanSeek
            ? stream
            : new CountingStream(stream, options.MaxFileSizeBytes);

        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(effective, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            throw new ReplayLoadException("Failed to read ASC stream", ex);
        }

        var (frames, origin, absolute) = ParseLines(lines);
        return new AscParseResult(frames, origin, absolute);
    }

    private static readonly char[] WhitespaceSeparators = { ' ', '\t' };




    /// <summary>
    /// v3.10.0 MINOR T4 (H5): read-only wrapper that counts cumulative
    /// bytes read from <see cref="_inner"/> and throws
    /// <see cref="ReplayLoadException"/> the moment the count exceeds
    /// <see cref="_maxBytes"/>. Forwards <see cref="Read(byte[], int, int)"/>
    /// + <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>; the
    /// ASC parse loop only needs the async overload (StreamReader uses
    /// it), but the sync overload is kept for completeness so the
    /// wrapper composes with any <see cref="Stream"/> consumer.
    /// <para>
    /// One-off inner helper — kept inside <c>AscParser.cs</c> rather than
    /// promoted to a new file. Mirrors the "avoid yet another abstraction"
    /// rule from the v3.10.0 MINOR plan.
    /// </para>
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _count;

        public CountingStream(Stream inner, long maxBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Accumulate(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Only count after the read completes so we don't accumulate
            // bytes that ended up not delivered (rare for forward-only
            // pipes, but the API contract is "bytes transferred").
            return AwaitAndCount(_inner.ReadAsync(buffer, offset, count, cancellationToken));
        }

        private async Task<int> AwaitAndCount(Task<int> task)
        {
            var n = await task.ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        private void Accumulate(int bytesRead)
        {
            if (bytesRead <= 0) return;
            _count += bytesRead;
            if (_count > _maxBytes)
            {
                throw new ReplayLoadException(
                    $"ASC stream exceeds size cap ({_count:N0} > {_maxBytes:N0} bytes)");
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    // === Flow A methods moved to AscParser/DataLineParserFlow.cs (W13 Task 1) ===
    // === Flow B methods moved to AscParser/ParseLinesFlow.cs (W13 Task 2) ===
}