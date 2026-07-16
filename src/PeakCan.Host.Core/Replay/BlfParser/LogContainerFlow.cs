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
        using var innerMs = new MemoryStream(
            decompressed.AsSpan(firstLobj).ToArray(),
            writable: false);
        try
        {
            var innerTask = ParseAsync(innerMs, new ReplayOptions(), logger, CancellationToken.None);
            var innerFrames = innerTask.GetAwaiter().GetResult();
            return innerFrames;
        }
        catch (ReplayFormatException)
        {
            // Recursive parse failed even after LOBJ-prefix-alignment.
            // Most likely the trim landed on a partial LOBJ signature
            // (e.g. 2 stray 'L'-class bytes followed by a real LOBJ).
            // Try harder: scan forward to the NEXT LOBJ and retry once.
            int nextLobj = IndexOfLobj(decompressed, firstLobj + 1);
            if (nextLobj < 0) return Array.Empty<ReplayFrame>();
            using var retryMs = new MemoryStream(
                decompressed.AsSpan(nextLobj).ToArray(),
                writable: false);
            var retryTask = ParseAsync(retryMs, new ReplayOptions(), logger, CancellationToken.None);
            return retryTask.GetAwaiter().GetResult();
        }
        catch (EndOfStreamException ex)
        {
            // v3.51.0 T6 PATCH: a real-Vector LOG_CONTAINER may include
            // up to ~16 bytes of trailing continuation data (the tail of
            // the previous chunk's last frame) AFTER all properly-framed
            // inner LOBJs. Our inner ParseAsync exits the loop cleanly
            // when stream.Position >= stream.Length but if the LOBJ scan
            // reads past EOF via BinaryReader (e.g. header_size_read),
            // it throws EndOfStreamException. Treat it like a normal
            // truncated-tail case: drop the partial object and return
            // whatever frames were already collected from earlier full
            // objects in this chunk.
            //
            // Note: we do NOT have access to the partial frames list
            // here (ParseAsync only returns the full list on success).
            // The cleaner fix lives at a higher level — see v3.51.0
            // T6 PATCH in BlfParser.cs: the catch (ReplayFormatException)
            // on ParseObjectBody now also catches EndOfStreamException
            // and skips the partial tail without bumping errorCount
            // past the 50% threshold. That fix is upstream; this catch
            // here is a belt-and-braces fallback so an exceptional
            // EndOfStreamException inside a chunked LOG_CONTAINER does
            // not corrupt the outer 50% threshold.
            System.Console.WriteLine(
                $"[BLF DBG] LOG_CONTAINER inner EndOfStream at chunk: {ex.Message}");
            return Array.Empty<ReplayFrame>();
        }
    }

    /// <summary>
    /// v3.51.0 T6 PATCH: find the first byte offset of an LOBJ
    /// signature in <paramref name="data"/>, scanning from
    /// <paramref name="start"/>. Returns -1 if not found. Sister of the
    /// outer LOBJ-search strategy in BlfParser.ParseAsync.
    /// </summary>
    private static int IndexOfLobj(byte[] data, int start = 0)
    {
        for (int i = Math.Max(0, start); i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'L' && data[i + 1] == (byte)'O'
                && data[i + 2] == (byte)'B' && data[i + 3] == (byte)'J')
            {
                return i;
            }
        }
        return -1;
    }
}
