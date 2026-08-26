using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.HIL.Core.Replay;

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
    /// &gt;50% corruption; throws ReplayLoadException on stream-size cap
    /// exceeded (via existing CountingStream path).
    /// <para>
    /// v3.17.0 PATCH (BLF playback fix): delegates to
    /// <see cref="ParseAsyncWithOrigin"/> and returns only
    /// <see cref="BlfParseResult.Frames"/> (already relativized + sorted).
    /// Preserves the original <c>IReadOnlyList&lt;ReplayFrame&gt;</c> return
    /// contract so the existing 14 call sites and tests are unaffected.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream,
        ReplayOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
        => (await ParseAsyncWithOrigin(stream, options, logger, ct).ConfigureAwait(false)).Frames;

    /// <summary>
    /// v3.17.0 PATCH (BLF playback fix): parse <paramref name="stream"/> as
    /// BLF and return both the frame list AND the wall-clock origin. Sister
    /// of <see cref="AscParser.ParseAsyncWithHeaderAsync"/>.
    /// <para>
    /// <b>Why relativize:</b> BLF object_time_stamp is 1-nanosecond ticks
    /// since the 1970 Vector epoch — an absolute value in the ~1.5e5-second
    /// range (~1.8 days) for real recordings. <see cref="ReplayFrame.Timestamp"/>'s
    /// contract is "seconds from recording start" (relative), and
    /// <see cref="ReplayTimeline"/>'s <c>PlayedTimestamp</c> grows from 0.
    /// Without relativization, <c>OnTick</c>'s <c>frame.Timestamp &lt;= now</c>
    /// predicate never matches (now needs ~1.8 days to reach the first
    /// frame) → 0 frames emit, slider/time frozen. Relativization subtracts
    /// the minimum absolute timestamp from every frame so the first-emitted
    /// frame is at t=0, matching the ASC + ReplayTimeline relative contract.
    /// </para>
    /// <para>
    /// <b>Why Min, not result[0]:</b> BLF object stream order is NOT
    /// guaranteed strictly increasing — LOG_CONTAINER recursion appends inner
    /// objects in container order, which may be out of absolute-time order.
    /// Using <c>result[0]</c> as the baseline can yield negative relative
    /// timestamps for earlier-time frames written later. Min is the robust
    /// baseline; inter-frame intervals (including gap windows with no
    /// messages) are preserved exactly because the shift is uniform.
    /// </para>
    /// <para>
    /// <b>Why single outer pass:</b> LogContainerFlow recurses into
    /// <see cref="ParseCoreAsync"/> per zlib chunk; relativization must NOT
    /// happen per-container (each chunk has its own min, so per-container
    /// relativization resets timestamps to ~0 at every chunk boundary,
    /// breaking the cross-container baseline). This public entry point
    /// is the outermost call — it delegates to ParseCoreAsync (raw parse,
    /// no relativization) then applies ONE uniform shift + sort across all
    /// frames from all containers.
    /// </para>
    /// <para>
    /// <b>WallClockOrigin:</b> the pre-relativization minimum absolute
    /// timestamp as a UTC DateTime (<c>VectorEpoch + minAbsoluteSeconds</c>).
    /// Reserved for future X-axis wall-clock display; no live consumer wires
    /// it yet (YAGNI).
    /// </para>
    /// </summary>
    public static async Task<BlfParseResult> ParseAsyncWithOrigin(
        Stream stream,
        ReplayOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var result = await ParseCoreAsync(stream, options, logger, ct).ConfigureAwait(false);

        if (result.Count == 0)
        {
            throw new ReplayFormatException("BLF file contains no parseable frames");
        }

        // v3.17.0 PATCH follow-up: ALWAYS shift, even when min==0. Real BLF
        // files commonly contain zero-timestamp objects (file-statistics
        // artifacts, metadata objects, or genuinely zero-tick frames). The
        // old `if (minTimestamp > 0.0)` guard skipped relativization when
        // such an object set min==0 — leaving every absolute-timestamp frame
        // at its raw ~1.5e5 value. A no-op shift (subtract 0) produces the
        // same result for all-zero fixtures anyway, so the guard was pure
        // downside on real files. Removed.
        double minTimestamp = result.Min(f => f.Timestamp);
        // Vector epoch = 1970-01-01 UTC per BlfFormat.TimestampScale xmldoc
        // ("1-nanosecond ticks since Vector epoch").
        var vectorEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? wallClockOrigin = vectorEpoch.AddSeconds(minTimestamp);
        var relativized = new List<ReplayFrame>(result.Count);
        foreach (var f in result)
        {
            relativized.Add(f with { Timestamp = f.Timestamp - minTimestamp });
        }
        result = relativized;

        // v3.17.0 PATCH follow-up: sort frames by Timestamp (ascending).
        // BLF object stream order is NOT guaranteed strictly increasing —
        // LOG_CONTAINER recursion appends inner objects in container order,
        // which can be out-of-absolute-time order. Without sort, two
        // invariants break:
        //   1. TotalDuration = _frames[^1].Timestamp (ReplayService.cs:55 /
        //      TraceViewerService.cs:88) takes the LAST element, not the max
        //      — so if the max-timestamp frame is mid-list, TotalDuration is
        //      too small and the UI slider Maximum tracks it, making playback
        //      time visually exceed TotalDuration mid-playback (user symptom:
        //      "时间超过 TotalDuration 还在涨").
        //   2. OnTick's `_nextFrameIndex >= _frames.Count` EOF check still
        //      fires (idx walks in list order), but the UI shows time >
        //      TotalDuration for any mid-list frame whose Timestamp >
        //      _frames[^1].Timestamp — "跑到结束不停" is the perceived symptom
        //      because time keeps climbing past the displayed TotalDuration
        //      until the true max frame emits and idx finally reaches Count.
        // Sister of AscParser.ParseLinesFlow.cs:70 `frames.Sort`. Sort is
        // applied AFTER relativization (the shift is uniform, so the relative
        // order is identical to the absolute order — sort either way yields
        // the same sequence).
        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        return new BlfParseResult(result, wallClockOrigin);
    }

    /// <summary>
    /// v3.17.0 PATCH follow-up: the raw BLF parse loop — scans for LOBJ,
    /// reads ObjectHeader, dispatches to per-frame unpackers, applies the
    /// 50% corruption threshold. Returns frames with their RAW absolute
    /// timestamps (1ns-ticks-since-Vector-epoch / TimestampScale seconds);
    /// does NOT relativize or sort. Used internally by ParseAsyncWithOrigin
    /// (which does the single outer relativization+sort pass) and by
    /// LogContainerFlow's per-chunk recursion (so each chunk's frames keep
    /// raw absolute timestamps and the outer caller can relativize them all
    /// against a single baseline).
    /// </summary>
    internal static async Task<List<ReplayFrame>> ParseCoreAsync(
        Stream stream,
        ReplayOptions options,
        ILogger? logger,
        CancellationToken ct)
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
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Search for LOBJ signature. If 4 bytes don't match LOBJ, rewind
            // 3 bytes and try again — this tolerates up to 3 padding bytes
            // between objects.
            long pos = stream.Position;
            while (stream.Position < stream.Length)
            {
                pos = stream.Position;
                int bytesAvailable = (int)Math.Min(4, stream.Length - stream.Position);
                if (bytesAvailable < 4)
                {
                    break; // Near EOF: cannot possibly match a 4-byte signature
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
            //   ObjectHeaderBase = "4sHHII": signature, header_size, header_version,
            //     object_size, object_type
            //   ObjectHeader = "IHHQ": object_flags, client_index, reserved,
            //     object_time_stamp (UINT64, 1ns ticks since Vector epoch)
            long objStart = stream.Position;
            string objSig;
            try
            {
                objSig = new string(reader.ReadChars(4));
            }
            catch (EndOfStreamException)
            {
                // Real Vector BLF often ends with a partial trailing object.
                break;
            }
            if (objSig != BlfFormat.ObjSignature)
            {
                break; // No LOBJ found (stream ended); exit
            }
            _ = reader.ReadUInt16();   // header_size
            _ = reader.ReadUInt16();   // header_version
            uint objectSize = reader.ReadUInt32();
            uint objectType = reader.ReadUInt32();
            // ObjectHeader extension (16 bytes)
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
                long objEnd = objStart + BlfFormat.ObjectHeaderSize + frameDataSize;
                if (objEnd <= stream.Length) stream.Position = objEnd;
            }

            // After SUCCESSFUL parse, position the stream at the end of this
            // object (objStart + objSize) so the next LOBJ search does NOT
            // re-enter the just-parsed object's payload.
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
