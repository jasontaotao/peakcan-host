using System.Buffers.Binary;
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
        // Recursively parse the decompressed byte stream. Per v3.51.0
        // T6 PATCH: real-Vector LOG_CONTAINERs split a trace across
        // multiple zlib chunks (~131072 bytes each uncompressed) so the
        // writer can resume after a crash. The boundary isn't always
        // LOBJ-aligned: the chunk may BEGIN with up to ~16 bytes of
        // continuation data from the previous chunk's last frame,
        // followed by a properly-framed LOBJ stream. The simple
        // recursive-ParseAsync (which expects 'LOGG' or 'LOBJ' as the
        // first 4 bytes) fails with `bad magic ''` on these chunks.
        //
        // The fix is to LOBJ-scan to the first valid LOBJ inside the
        // decompressed buffer before recursing. We pad the buffer with
        // 3 dummy bytes after a couple of LOBJs in the decomposition
        // step... no — better to just retry with an LOBJ-pre-aligned
        // synthetic prefix in a contained wrapper. v3.51.0 T6 chose the
        // minimal approach: find the first LOBJ in the buffer and trim
        // everything before it.
        int firstLobj = IndexOfLobj(decompressed);
        if (firstLobj < 0)
        {
            // No real LOBJ found (container's continuation chunk is
            // pure tail bytes with no new framed messages). Return
            // empty — the upstream caller will keep accumulating frames
            // from later chunks that do contain real LOBJs.
            return Array.Empty<ReplayFrame>();
        }
        var innerBytes = decompressed.AsSpan(firstLobj).ToArray();
        try
        {
            using var innerMs = new MemoryStream(innerBytes, writable: false);
            var innerTask = ParseAsync(innerMs, new ReplayOptions(), logger, CancellationToken.None);
            return innerTask.GetAwaiter().GetResult();
        }
        catch (ReplayFormatException ex)
        {
            // v3.51.0 T6.6 PATCH (user-reported data-loss bug): the
            // previous T6 implementation's "retry from next LOBJ"
            // branch silently discarded any frames successfully
            // parsed BEFORE the throwing point — losing ~30K frames
            // (~32.8% of a 100K-frame real-Vector BLF). The new
            // strategy is to run a non-throwing lenient scan that
            // recovers every parseable frame and silently skips any
            // unparseable object instead of raising on the 50%
            // threshold. v3.51.0 T6.6 documented via release notes.
            _logger.LogWarning(
                "BLF container inner ReplayFormatException: {Reason}. " +
                "Switching to lenient scan to preserve partial frames.",
                ex.Message);
            return LenientScan(innerBytes);
        }
        catch (EndOfStreamException ex)
        {
            // v3.51.0 T6.6 PATCH: same lenient-scan fallback as the
            // ReplayFormatException branch — preserve partial frames
            // rather than discarding the whole container's yield.
            _logger.LogWarning(
                "BLF container inner EndOfStream: {Reason}. " +
                "Lenient scan to preserve partial frames.",
                ex.Message);
            return LenientScan(innerBytes);
        }
    }

    /// <summary>
    /// v3.51.0 T6.6 PATCH (user-reported data-loss): a non-throwing
    /// variant of ParseAsync that returns every successfully-parsed
    /// frame and silently skips any object that fails to parse (no
    /// 50% threshold, no exception). Used ONLY by LogContainerFlow
    /// as a fallback when the main recursive path throws, so we
    /// preserve partial-frame yield across partially-corrupt inner
    /// streams rather than throwing away 100% of a container's
    /// frames. Mirrors the LOBJ-scan strategy of BlfParser.ParseAsync
    /// but with lenient object-skip instead of threshold throw.
    /// </summary>
    private static IReadOnlyList<ReplayFrame> LenientScan(byte[] innerBytes)
    {
        var result = new List<ReplayFrame>();
        int i = 0;
        while (i < innerBytes.Length)
        {
            // Find LOBJ at or after offset i
            int lobj = IndexOfLobjFrom(innerBytes, i);
            if (lobj < 0) break;
            // 32-byte header
            if (lobj + BlfFormat.ObjectHeaderSize > innerBytes.Length) break;
            uint objectSize = BinaryPrimitives.ReadUInt32LittleEndian(innerBytes.AsSpan(lobj + 8));
            uint objectType = BinaryPrimitives.ReadUInt32LittleEndian(innerBytes.AsSpan(lobj + 12));
            ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(innerBytes.AsSpan(lobj + 24));
            int frameDataSize = (int)objectSize - BlfFormat.ObjectHeaderSize;
            if (frameDataSize < 0
                || lobj + BlfFormat.ObjectHeaderSize + frameDataSize > innerBytes.Length)
            {
                i = lobj + 1;  // bogus header; slide forward 1
                continue;
            }
            try
            {
                var subSpan = innerBytes.AsSpan(
                    lobj + BlfFormat.ObjectHeaderSize, frameDataSize);
                var frames = ParseObjectBody(objectType, timestamp, subSpan);
                foreach (var f in frames) result.Add(f);
                i = lobj + BlfFormat.ObjectHeaderSize + frameDataSize;
            }
            catch (ReplayFormatException)
            {
                // Unparseable object inside this chunk — slide
                // forward 1 byte so we resume at the next byte in
                // case the LOBJ match was an in-padded false match.
                i = lobj + 1;
            }
        }
        return result;
    }

    /// <summary>
    /// v3.51.0 T6.6 PATCH: find the first LOBJ signature at offset
    /// &gt;= <paramref name="start"/>. Returns -1 if not found.
    /// </summary>
    private static int IndexOfLobjFrom(byte[] data, int start)
    {
        for (int i = Math.Max(0, start); i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'L' && data[i+1] == (byte)'O'
                && data[i+2] == (byte)'B' && data[i+3] == (byte)'J')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// v3.51.0 T6 PATCH: find the first byte offset of an LOBJ
    /// signature in <paramref name="data"/> starting at offset 0.
    /// Returns -1 if not found. (Sister of BlfParser.ParseAsync's
    /// outer-LOBJ-search but exposed here so the LOBJ-prefix-align
    /// step doesn't have to scan entire tree twice.) v3.51.0 T6.6
    /// added IndexOfLobjFrom(byte[], int) for the lenient-scan
    /// fallback; this overload remains as the simpler entry point.
    /// </summary>
    private static int IndexOfLobj(byte[] data)
    {
        return IndexOfLobjFrom(data, 0);
    }
}
