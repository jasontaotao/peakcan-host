using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    /// <summary>
    /// v3.51.0 MINOR (T2.1): decompresses zlib LOG_CONTAINER and recursively
    /// parses its content. Sister of vblf_reader.py:128-142.
    /// Container layout per vblf_general.py:447-450 LogContainer.unpack:
    ///   header = ObjectHeader.unpack_from(buffer)
    ///   return cls(header, buffer[ObjectHeader.SIZE :])
    /// i.e. the entire frame data immediately after the 32-byte ObjectHeader
    /// is the raw compressed (or uncompressed) payload — there is NO
    /// 4-byte compression_level + 4-byte reserved prefix per container.
    /// The compression level lives in the file-level FileStatistics header
    /// (vblf_reader.py:133) and is not duplicated inside each container.
    /// </summary>
    internal static IReadOnlyList<ReplayFrame> LogContainerFlow_UnpackAndRecurse(
        ReadOnlySpan<byte> frameData, ILogger logger)
    {
        if (frameData.Length < 4)
        {
            throw new ReplayFormatException(
                $"LogContainer frame too small: {frameData.Length} < 4");
        }
        // Per vblf_general.py:450, frameData IS the entire payload (zlib-compressed
        // inner objects). Always try zlib decompression; if the payload was written
        // uncompressed the ZLibStream call will fail naturally with a format error.
        var compressed = frameData.ToArray();
        byte[] decompressed;
        using (var input = new MemoryStream(compressed))
        using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            zlib.CopyTo(output);
            decompressed = output.ToArray();
        }
        // Recursively parse the decompressed byte stream
        using var innerMs = new MemoryStream(decompressed);
        var innerTask = ParseAsync(innerMs, new ReplayOptions(), logger, CancellationToken.None);
        var innerFrames = innerTask.GetAwaiter().GetResult();
        // Return all frames from the decompressed container (sister of vblf's
        // recursive yield from, which yields every frame in the container).
        return innerFrames;
    }
}
