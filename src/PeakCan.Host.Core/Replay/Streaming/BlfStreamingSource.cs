using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming BLF source: reads object headers eagerly and decodes CAN frame
/// objects lazily. Compressed LOG_CONTAINER payloads are unpacked on demand;
/// frames are passed through a bounded reorder window. Unlike
/// <see cref="BlfParser"/>, the source never materializes the whole file.
/// </summary>
public sealed class BlfStreamingSource : IStreamingTraceSource
{
    private readonly Func<Stream> _streamFactory;
    private readonly ILogger _logger;

    public BlfStreamingSource(Func<Stream> streamFactory, ILogger? logger = null)
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
            if (stream.CanSeek && stream.Length < 4)
                throw new ReplayFormatException($"BLF file too small: {stream.Length} bytes");
            if (!stream.CanSeek)
                throw new ReplayFormatException("BLF streaming source requires a seekable stream.");

            var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var firstSignature = new string(reader.ReadChars(4));
            if (firstSignature == BlfFormat.FileSignature)
            {
                if (stream.Length < BlfFormat.FileHeaderSize)
                    throw new ReplayFormatException($"BLF file too small: {stream.Length} bytes");
                reader.ReadBytes(BlfFormat.FileHeaderSize - 4);
            }
            else if (firstSignature == BlfFormat.ObjSignature)
            {
                stream.Position -= 4;
            }
            else
            {
                throw new ReplayFormatException(
                    $"Not a valid BLF file: bad magic '{firstSignature}' (expected '{BlfFormat.FileSignature}' or '{BlfFormat.ObjSignature}')");
            }

            var stats = new StreamingParseStats();
            return new StreamingTraceOpenResult
            {
                Frames = Enumerate(stream, stats, skipUntil, ct),
                SourceLengthBytes = stream.Length,
                Stats = stats,
                SourceStream = stream,
            };
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async IAsyncEnumerable<ReplayFrame> Enumerate(
        Stream stream,
        StreamingParseStats stats,
        double? skipUntil,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            var reorder = new BlfReorderBuffer();
            long objectCount = 0, errorCount = 0;

            while (await FindLobjAsync(stream, stats, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                objectCount++;

                var header = ReadObjectHeader(stream, stats);
                if (header is null || header.Value.ObjectSize < BlfFormat.ObjectHeaderSize)
                {
                    errorCount++;
                    continue;
                }

                var frameDataSize = (int)(header.Value.ObjectSize - BlfFormat.ObjectHeaderSize);
                if (frameDataSize < 0)
                {
                    errorCount++;
                    continue;
                }

                var frameData = await ReadExactlyAsync(stream, frameDataSize, stats, ct).ConfigureAwait(false);
                if (frameData.Length != frameDataSize)
                {
                    errorCount++;
                    break;
                }

                IReadOnlyList<ReplayFrame> parsed;
                try
                {
                    parsed = header.Value.ObjectType == BlfFormat.ObjTypeLogContainer
                        ? BlfParser.LogContainerFlow_UnpackAndRecurse(frameData, _logger)
                        : BlfParser.ParseObjectBody(header.Value.ObjectType, header.Value.Timestamp, frameData);
                }
                catch (ReplayFormatException ex)
                {
                    errorCount++;
                    _logger.LogDebug(ex, "Skipped corrupted BLF object at {Offset}", stream.Position);
                    continue;
                }

                foreach (var frame in parsed)
                {
                    if (skipUntil.HasValue && frame.Timestamp < skipUntil.Value) continue;
                    foreach (var ready in reorder.Push(frame))
                        yield return ready;
                }

                if (errorCount * 2 > objectCount)
                    throw new ReplayFormatException($"BLF corruption: {errorCount}/{objectCount} objects failed (>{50}%)");
            }

            foreach (var frame in reorder.Flush())
                yield return frame;
        }
        finally
        {
            // StreamingTraceOpenResult owns SourceStream disposal.
        }
    }

    private static async Task<bool> FindLobjAsync(Stream stream, StreamingParseStats stats, CancellationToken ct)
    {
        var signature = new byte[4];
        var length = 0;
        int b;
        while ((b = await stream.ReadByteAsync(ct).ConfigureAwait(false)) >= 0)
        {
            stats.AddBytes(1);
            signature[length % 4] = (byte)b;
            length++;
            if (length >= 4 &&
                signature[0] == (byte)'L' && signature[1] == (byte)'O' &&
                signature[2] == (byte)'B' && signature[3] == (byte)'J')
            {
                stream.Position -= 4;
                return true;
            }
        }
        return false;
    }

    private static (uint ObjectSize, uint ObjectType, ulong Timestamp)? ReadObjectHeader(Stream stream, StreamingParseStats stats)
    {
        var buffer = ReadExactly(stream, BlfFormat.ObjectHeaderSize);
        if (buffer.Length < BlfFormat.ObjectHeaderSize) return null;
        stats.AddBytes(BlfFormat.ObjectHeaderSize);
        var objectSize = BitConverter.ToUInt32(buffer, 8);
        var objectType = BitConverter.ToUInt32(buffer, 12);
        var timestamp = BitConverter.ToUInt64(buffer, 24);
        return (objectSize, objectType, timestamp);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var result = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(result, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return read == count ? result : result[..read];
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream, int count, StreamingParseStats stats, CancellationToken ct)
    {
        var result = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(result.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        stats.AddBytes(read);
        return read == count ? result : result[..read];
    }
}

internal static class StreamBlfExtensions
{
    public static async ValueTask<int> ReadByteAsync(this Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }
}
