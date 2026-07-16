using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: parses Vector BLF trace files. Sister of v3.49.0
/// AscParser. Pure .NET, no Vector SDK dependency. Strict error
/// handling (sister of v3.50.5): bad magic → ReplayFormatException,
/// >50% corrupted frames → ReplayFormatException, truncated stream
/// → ReplayFormatException. Algorithm sister of vblf._generate_objects:
/// scan for LOBJ signature, parse ObjectHeaderBase + ObjectHeader
/// extension (IHHQ), dispatch by object_type to per-frame-class
/// unpacker.
/// </summary>
public static partial class BlfParser
{
    private static ILogger _logger = NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Warning,
                   Message = "Skipped unknown BLF object type {ObjectType} at offset {Offset}")]
    private static partial void LogUnknownObject(ILogger logger, uint objectType, long offset);

    [LoggerMessage(Level = LogLevel.Warning,
                   Message = "Skipped corrupted BLF frame at offset {Offset}: {Reason}")]
    private static partial void LogCorruptedFrame(ILogger logger, long offset, string reason);

    /// <summary>
    /// v3.51.0 MINOR: parse <paramref name="stream"/> as BLF. Sister of
    /// AscParser.ParseAsync. Throws ReplayFormatException on bad magic /
    /// >50% corruption; throws ReplayLoadException on stream-size cap
    /// exceeded (via existing CountingStream path).
    /// </summary>
    public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        ReplayOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger.Instance;

        if (stream.CanSeek && stream.Length < 4)
        {
            throw new ReplayFormatException($"BLF file too small: {stream.Length} bytes");
        }

        var result = new List<ReplayFrame>();
        int objectCount = 0;
        int errorCount = 0;

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        // 1. Detect format: starts with LOGG (full file with FileStatistics) or
        //    LOBJ (raw object stream, e.g. vblf test fixture or decompressed container).
        //    This mirrors vblf_reader.py where the reader expects FileStatistics
        //    prefix, but also lets us parse object-only streams (LOG_CONTAINER
        //    decompressed payload, vblf_test_CAN_MESSAGE.lobj unit-test fixture).
        long firstSigPos = stream.Position;
        string fileSig = new string(reader.ReadChars(4));
        if (fileSig == BlfFormat.FileSignature)
        {
            // Full BLF file: skip the 144-byte FileStatistics metadata after the 4-byte LOGG magic.
            if (stream.CanSeek && stream.Length < BlfFormat.FileHeaderSize)
            {
                throw new ReplayFormatException($"BLF file too small: {stream.Length} bytes");
            }
            reader.ReadBytes(BlfFormat.FileHeaderSize - 4);
        }
        else if (fileSig == BlfFormat.ObjSignature)
        {
            // Raw object stream: rewind to start of LOBJ, no FileStatistics to skip.
            stream.Position = firstSigPos;
        }
        else
        {
            throw new ReplayFormatException(
                $"Not a valid BLF file: bad magic '{fileSig}' (expected '{BlfFormat.FileSignature}' or '{BlfFormat.ObjSignature}')");
        }

        // 2. Object stream parse loop (sister of vblf._generate_objects)
        // Algorithm: scan for LOBJ signature, then read full 32-byte ObjectHeader
        // (16-byte ObjectHeaderBase + 16-byte IHHQ extension per vblf_general.py),
        // then dispatch by object_type to per-frame-class unpacker.
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Search for LOBJ signature (sister of vblf reader line 97-105).
            // If 4 bytes don't match LOBJ, rewind 3 bytes and try again — this
            // tolerates up to 3 padding bytes between objects.
            long pos = stream.Position;
            while (stream.Position < stream.Length)
            {
                pos = stream.Position;
                int bytesAvailable = (int)Math.Min(4, stream.Length - stream.Position);
                if (bytesAvailable < 4)
                {
                    // Near EOF: cannot possibly match a 4-byte signature
                    break;
                }
                string sig = new string(reader.ReadChars(4));
                if (sig == BlfFormat.ObjSignature)
                {
                    stream.Position = pos; // rewind to LOBJ start
                    break;
                }
                // Not LOBJ: rewind 3 bytes (we read 4) and try again at next byte
                stream.Seek(-3, SeekOrigin.Current);
            }
            if (stream.Position >= stream.Length) break;

            // Read ObjectHeaderBase (16 bytes) + ObjectHeader extension (16 bytes) = 32 bytes.
            // ObjectHeaderBase._FORMAT = struct.Struct("4sHHII") = 16 bytes:
            //   4s = signature (LOBJ), H = header_size, H = header_version,
            //   I = object_size, I = object_type
            // ObjectHeader._FORMAT = struct.Struct("IHHQ") = 16 bytes:
            //   I = object_flags, H = client_index, H = reserved/object_version,
            //   Q = object_time_stamp (UINT64, 10ns ticks since Vector epoch)
            long objStart = stream.Position;
            string objSig;
            try
            {
                objSig = new string(reader.ReadChars(4));
            }
            catch (EndOfStreamException)
            {
                // v3.51.0 T6 PATCH: real Vector BLF often ends with a
                // partial trailing object (recording cut off mid-write
                // or partial zlib trailer). The previous behavior was
                // to fall through to the next iteration with a partial
                // header, then trip the 50% corruption threshold. Now
                // we exit cleanly so the caller gets all fully-parsed
                // frames.
                break;
            }
            if (objSig != BlfFormat.ObjSignature)
            {
                // No LOBJ found (stream ended); exit
                break;
            }
            ushort headerSize = reader.ReadUInt16();
            ushort headerVersion = reader.ReadUInt16();
            uint objectSize = reader.ReadUInt32();
            uint objectType = reader.ReadUInt32();
            // ObjectHeader extension (16 bytes) — read all so the stream is
            // positioned at the start of frame data.
            _ = reader.ReadUInt32();   // object_flags
            _ = reader.ReadUInt16();   // client_index
            _ = reader.ReadUInt16();   // reserved / object_version
            ulong timestamp = reader.ReadUInt64();

            objectCount++;

            // Frame data size = total object size - 32-byte ObjectHeader
            int frameDataSize = (int)objectSize - BlfFormat.ObjectHeaderSize;
            if (frameDataSize < 0)
            {
                errorCount++;
                LogCorruptedFrame(_logger, objStart, $"object_size {objectSize} smaller than ObjectHeaderSize {BlfFormat.ObjectHeaderSize}");
                continue;
            }

            try
            {
                // Read exactly frameDataSize bytes of frame data into a buffer.
                // This isolates the unpacker from stream-position issues (e.g.
                // truncated objects where stream.Read would happily read past
                // the current object into the next object's header).
                byte[] frameData = new byte[frameDataSize];
                int totalRead = 0;
                while (totalRead < frameDataSize)
                {
                    int n = stream.Read(frameData, totalRead, frameDataSize - totalRead);
                    if (n == 0) break;
                    totalRead += n;
                }
                if (totalRead < frameDataSize)
                {
                    throw new ReplayFormatException(
                        $"object truncated: expected {frameDataSize} bytes, got {totalRead}");
                }
                var frames = ParseObjectBody(objectType, timestamp, frameData);
                foreach (var f in frames) result.Add(f);
            }
            catch (ReplayFormatException ex)
            {
                errorCount++;
                LogCorruptedFrame(_logger, objStart, ex.Message);
                // Seek to the end of this object (clamped to stream length) so
                // the next LOBJ search starts cleanly.
                long objEnd = objStart + BlfFormat.ObjectHeaderSize + frameDataSize;
                if (objEnd <= stream.Length) stream.Position = objEnd;
            }

            // v3.51.0 T6 PATCH: after SUCCESSFUL parse, position the
            // stream at the end of this object (objStart + objSize) so
            // the next iteration's LOBJ search does NOT re-enter the
            // just-parsed object's payload. Without this, real Vector
            // BLF files (where multiple LOG_CONTAINERs are chained with
            // optional zlib trailer padding) hit a false-positive
            // 50% corruption threshold: a successful LOG_CONTAINER
            // leaves the stream at the byte-after-end of the parsed
            // payload, but if that byte happens to be the first byte
            // of the next container's LOBJ+header (with a few bytes of
            // inter-container padding merged) the outer scanner
            // either skips past real content (under-count) or matches
            // a partial LOBJ signature that fails to parse (over-count).
            // objSize is the trusted byte budget set by the writer; we
            // use it as the absolute cap. Clamp to stream length so we
            // don't seek past EOF.
            long successEnd = objStart + objectSize;
            if (stream.CanSeek && successEnd <= stream.Length)
            {
                stream.Position = successEnd;
            }

            // 50% corruption threshold (sister of v3.50.5 + AscParser.ParseLinesFlow)
            if (objectCount > 0 && errorCount * 2 > objectCount)
            {
                throw new ReplayFormatException(
                    $"BLF corruption: {errorCount}/{objectCount} objects failed (>{50}%)");
            }
        }

        if (result.Count == 0)
        {
            throw new ReplayFormatException("BLF file contains no parseable frames");
        }

        return result;
    }

    private static IReadOnlyList<ReplayFrame> ParseObjectBody(
        uint objectType, ulong timestamp, ReadOnlySpan<byte> frameData)
    {
        return objectType switch
        {
            BlfFormat.ObjTypeCanMessage =>
                new[] { CanMessageFlow_Unpack(timestamp, frameData) },
            BlfFormat.ObjTypeCanMessage2 =>
                new[] { CanMessage2Flow_Unpack(timestamp, frameData) },
            BlfFormat.ObjTypeCanFdMessage =>
                new[] { CanFdMessageFlow_Unpack(timestamp, frameData) },
            BlfFormat.ObjTypeCanFdMessage64 =>
                new[] { CanFdMessage64Flow_Unpack(timestamp, frameData) },
            BlfFormat.ObjTypeLogContainer =>
                LogContainerFlow_UnpackAndRecurse(frameData, _logger),
            _ => Array.Empty<ReplayFrame>(), // unknown obj_type → skip
        };
    }
}
