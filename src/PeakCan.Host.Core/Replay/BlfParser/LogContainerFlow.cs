using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    /// <summary>
    /// v3.51.0 MINOR: decompresses zlib LOG_CONTAINER and recursively
    /// parses its content. Sister of vblf_reader.py:128-142.
    /// Container layout (per vblf general): 4-byte compression level +
    /// 4-byte reserved + zlib-compressed payload.
    /// </summary>
    internal static IReadOnlyList<ReplayFrame> LogContainerFlow_UnpackAndRecurse(
        ReadOnlySpan<byte> frameData, ILogger logger)
    {
        if (frameData.Length < 8)
        {
            throw new ReplayFormatException(
                $"LogContainer frame too small: {frameData.Length} < 8");
        }
        // First 4 bytes: compression level (1=zlib, 0=none)
        uint compressionLevel = BitConverter.ToUInt32(frameData.Slice(0, 4));
        // Skip 4 bytes reserved
        var compressed = frameData.Slice(8).ToArray();
        byte[] decompressed;
        if (compressionLevel > 0)
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            decompressed = output.ToArray();
        }
        else
        {
            decompressed = compressed;
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
