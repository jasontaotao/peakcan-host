using System.Globalization;
using IOPath = System.IO.Path;

namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// A parsed, in-memory firmware image ready for streaming to the ECU via
/// RequestDownload (0x34) + TransferData (0x36) + RequestTransferExit (0x37).
/// Owns only the payload bytes and total length; the destination memory
/// address is supplied separately by <c>FlashProfile.MemoryAddress</c>,
/// keeping addressing and data orthogonal (per Phase 1 scope decision
/// 2026-07-22 — raw-binary format only, address in profile).
/// </summary>
public sealed record FirmwareImage
{
    /// <summary>
    /// The firmware payload, as a defensive copy independent of any caller buffer.
    /// PipelineExecutor slices this into <c>TransferDataAsync</c> chunks sized by the
    /// ECU-reported block length (RequestDownloadAsync return value, TransferFlow.cs).
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Total payload length in bytes. Fed to <c>RequestDownloadAsync(address, length, ct)</c>
    /// as the <c>length</c> argument. Always equals <see cref="Data"/>.Length.
    /// </summary>
    public required uint Length { get; init; }
}

/// <summary>
/// Parses a firmware file into a <see cref="FirmwareImage"/>. Phase 1 supports
/// raw binary only — the file's bytes ARE the flash data payload. Intel HEX and
/// Motorola S-record formats arrive in Phase 1.1 via a format-detecting
/// overload; this class keeps the raw entry point as the stable surface.
/// </summary>
/// <summary>
/// Configurable CRC-32 parameters. Different ECUs/OEMs use different polynomial / init /
/// final-XOR / reflection combinations — this record captures the full parameterization so
/// the Verify step can match whatever the target ECU expects.
/// <para>
/// <b>Polynomial</b> is stored in the <i>normal</i> (datasheet / MSB-first) form — e.g.
/// 0x04C11DB7 for CRC-32, 0x1EDC6F41 for CRC-32C — so the operator can copy values straight
/// from the ECU datasheet. When <see cref="ReflectInput"/> is true, the implementation
/// reflects the polynomial internally to derive the reflected form used by the table
/// algorithm (e.g. 0x04C11DB7 → 0xEDB88320).
/// </para>
/// <para>
/// <see cref="ReflectInput"/> / <see cref="ReflectOutput"/> control bit-level reversal of each
/// input byte and of the final CRC value. Most "standard" CRC-32 variants (CRC-32,
/// CRC-32C) reflect both; CRC-32/MPEG-2 and CRC-32/BZIP2 reflect neither.
/// </para>
/// </summary>
/// <param name="Polynomial">The CRC polynomial in normal (datasheet) form — e.g. 0x04C11DB7 for CRC-32.</param>
/// <param name="Init">Initial register value (e.g. 0xFFFFFFFF).</param>
/// <param name="FinalXor">Value XORed with the register after processing (e.g. 0xFFFFFFFF).</param>
/// <param name="ReflectInput">Reverse bits of each input byte before processing.</param>
/// <param name="ReflectOutput">Reverse bits of the final register before FinalXor.</param>
public sealed record CrcParameters(
    uint Polynomial,
    uint Init,
    uint FinalXor,
    bool ReflectInput,
    bool ReflectOutput)
{
    /// <summary>CRC-32 / ISO-HDLC / ZIP / Ethernet (the de-facto standard).</summary>
    public static readonly CrcParameters Crc32 = new(0x04C11DB7, 0xFFFFFFFF, 0xFFFFFFFF, true, true);

    /// <summary>CRC-32C / Castagnoli (iSCSI, SCTP, NVMe — used by some ECUs).</summary>
    public static readonly CrcParameters Crc32C = new(0x1EDC6F41, 0xFFFFFFFF, 0xFFFFFFFF, true, true);

    /// <summary>CRC-32/MPEG-2 (no reflection, final XOR 0x00000000).</summary>
    public static readonly CrcParameters Crc32Mpeg2 = new(0x04C11DB7, 0xFFFFFFFF, 0x00000000, false, false);

    /// <summary>CRC-32 / BZIP2 (no reflection, final XOR 0xFFFFFFFF).</summary>
    public static readonly CrcParameters Crc32Bzip2 = new(0x04C11DB7, 0xFFFFFFFF, 0xFFFFFFFF, false, false);

    /// <summary>Named presets for the UI dropdown (order matters — index-bound).</summary>
    public static IReadOnlyList<CrcParameters> Presets { get; } =
        [Crc32, Crc32C, Crc32Mpeg2, Crc32Bzip2];

    /// <summary>Human-readable preset names, parallel to <see cref="Presets"/>.</summary>
    public static IReadOnlyList<string> PresetNames { get; } =
        ["CRC-32 (ISO-HDLC)", "CRC-32C (Castagnoli)", "CRC-32/MPEG-2", "CRC-32/BZIP2"];
}

/// <summary>
/// Phase 2: CRC computation with configurable parameters. Used to auto-compute per-segment
/// checksums for Verify-step comparison. The parameter-less <see cref="Compute(byte[])"/>
/// overload uses the standard CRC-32/ISO-HDLC parameters for backward compatibility.
/// <para>
/// Two algorithm families, selected by <see cref="CrcParameters.ReflectInput"/>:
/// <b>Reflected</b> (CRC-32, CRC-32C): table seeded with the raw byte value, LSB-first
/// reduction using the <i>reflected</i> polynomial (e.g. 0xEDB88320), compute via
/// <c>(crc ^ b) &amp; 0xFF</c> + right shift. Input reflection is implicit in the table
/// structure — no per-byte <see cref="Reflect8"/> call needed.
/// <b>Non-reflected</b> (CRC-32/MPEG-2, CRC-32/BZIP2): table seeded with the byte in the
/// MSB position, MSB-first reduction using the <i>normal</i> polynomial (e.g. 0x04C11DB7),
/// compute via <c>((crc >> 24) ^ b) &amp; 0xFF</c> + left shift.
/// </para>
/// </summary>
public static class Crc32
{
    /// <summary>
    /// Compute CRC using standard CRC-32/ISO-HDLC parameters (polynomial 0xEDB88320,
    /// init 0xFFFFFFFF, final XOR 0xFFFFFFFF, full reflection). Backward-compatible
    /// with all existing call sites.
    /// </summary>
    public static uint Compute(byte[] data) => Compute(data, CrcParameters.Crc32);

    /// <summary>
    /// Compute CRC using custom <paramref name="parms"/>. The table is generated per
    /// parameterization (cached) so different ECU CRC variants can coexist.
    /// <para>
    /// When <see cref="CrcParameters.ReflectInput"/> is true, the polynomial is reflected
    /// internally (normal → reflected form) and the reflected table algorithm is used
    /// (LSB-first, right shift). When false, the normal polynomial drives the non-reflected
    /// algorithm (MSB-first, left shift). This lets the operator enter datasheet (normal)
    /// polynomial values directly.
    /// </para>
    /// </summary>
    public static uint Compute(byte[] data, CrcParameters parms)
    {
        var table = TableCache.GetOrAdd(parms, GenerateTable);
        uint crc = parms.Init;
        if (parms.ReflectInput)
        {
            // Reflected algorithm: table lookup on the low byte + right shift.
            foreach (var b in data)
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        else
        {
            // Non-reflected algorithm: table lookup on the high byte + left shift.
            foreach (var b in data)
                crc = table[((crc >> 24) ^ b) & 0xFF] ^ (crc << 8);
        }
        // Output reflection: only needed when refOut differs from refIn (rare mixed modes).
        // When refIn==refOut (all common presets), the algorithm family already yields the
        // correct orientation, so no explicit Reflect32 is required.
        if (parms.ReflectOutput != parms.ReflectInput)
            crc = Reflect32(crc);
        return crc ^ parms.FinalXor;
    }

    private static uint[] GenerateTable(CrcParameters p)
    {
        // Reflected algorithm needs the reflected polynomial; the operator supplies the
        // normal (datasheet) form, so reflect it here when appropriate.
        uint poly = p.ReflectInput ? Reflect32(p.Polynomial) : p.Polynomial;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            // Reflected: seed with the raw byte value (reflection is implicit in the
            // LSB-first reduction). Non-reflected: seed with the byte in MSB position.
            uint c = p.ReflectInput ? i : (i << 24);
            for (int j = 0; j < 8; j++)
            {
                c = p.ReflectInput
                    ? (((c & 1) != 0) ? (poly ^ (c >> 1)) : (c >> 1))
                    : (((c & 0x80000000) != 0) ? ((c << 1) ^ poly) : (c << 1));
            }
            table[i] = c;
        }
        return table;
    }

    private static uint Reflect32(uint v)
    {
        uint r = 0;
        for (int i = 0; i < 32; i++)
        {
            if ((v & 1) != 0) r |= (1u << (31 - i));
            v >>= 1;
        }
        return r;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CrcParameters, uint[]> TableCache = new();
}

public static class FirmwareFileParser
{
    /// <summary>
    /// Parse a raw-binary firmware payload into a <see cref="FirmwareImage"/>.
    /// The returned <see cref="FirmwareImage.Data"/> is a defensive copy — mutating
    /// the caller's array afterwards does not affect the image.
    /// </summary>
    /// <param name="bytes">The raw firmware bytes. Must not be null or empty.</param>
    /// <returns>A <see cref="FirmwareImage"/> holding a defensive copy and the total length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is empty.</exception>
    public static FirmwareImage Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            // A zero-length firmware is never legitimate — RequestDownload(addr, 0)
            // would make the ECU enter a TransferData loop with zero work, and some
            // ECU implementations NRC an empty download outright. Refuse early.
            throw new ArgumentException(
                "Firmware payload is empty — a zero-length image cannot be downloaded.", nameof(bytes));
        }

        // Defensive copy so the ECU-bound payload cannot be silently mutated by the
        // caller reusing its source buffer mid-flash (which would corrupt TransferData chunks).
        var copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, bytes.Length);

        return new FirmwareImage
        {
            Data = copy,
            Length = (uint)bytes.Length,
        };
    }

    // ---- Phase 2: File-level parsing (HEX / S19 / raw binary) ----

    /// <summary>
    /// Parse a firmware file into a <see cref="FirmwareFile"/> with one or more
    /// address-contiguous segments. Format auto-detected from extension.
    /// </summary>
    public static FirmwareFile ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var ext = IOPath.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".hex" or ".ihx" => ParseIntelHex(path),
            ".s19" or ".srec" or ".mot" => ParseMotorolaS19(path),
            _ => ParseRawBinary(path),
        };
    }

    /// <summary>
    /// Parse an Intel HEX file. Handles record types 00 (data), 01 (EOF),
    /// 04 (extended linear address). Non-contiguous address regions become
    /// separate segments; adjacent records are merged.
    /// </summary>
    public static FirmwareFile ParseIntelHex(string path)
    {
        var lines = System.IO.File.ReadAllLines(path);
        var segments = new List<Segment>();
        uint baseAddress = 0;  // set by type 04 records
        uint? currentAddr = null;
        var currentData = new List<byte>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != ':')
                continue;

            // Phase 2 S1: Validate checksum before parsing (Intel HEX: two's complement of sum).
            ValidateIntelHexChecksum(line);

            var byteCount = ParseHexByte(line, 1);
            var address = (uint)(ParseHexByte(line, 3) << 8 | ParseHexByte(line, 5));
            var recordType = ParseHexByte(line, 7);

            if (recordType == 0x01)  // EOF
                break;

            if (recordType == 0x02)  // extended segment address — IAR/Keil 常用.
            {
                // Type 02: 段基址 = (HH << 8 | LL) << 4, 地址 = segmentBase + recordAddress.
                // 与 type 04 一样不 flush, 由下一条数据记录的连续性检查决定是否拆段.
                var newBase = (uint)(ParseHexByte(line, 9) << 8 | ParseHexByte(line, 11)) << 4;
                if (newBase != baseAddress)
                    baseAddress = newBase;
                continue;
            }

            if (recordType == 0x04)  // extended linear address — GCC/ARMCC 常用.
            {
                var newBase = (uint)(ParseHexByte(line, 9) << 8 | ParseHexByte(line, 11)) << 16;
                if (newBase != baseAddress)
                {
                    // Base address changed. Do NOT flush — just update the base and let the
                    // NEXT data record's continuity check (line ~283) decide whether to split.
                    // If the next data is truly non-contiguous (absAddr != expectedNext), the
                    // continuity check will flush. If it IS continuous (e.g. type 04 increments
                    // base from 0x0800→0x0801 for the next 64KB page), the data merges into
                    // one continuous segment as the operator expects.
                    baseAddress = newBase;
                }
                // If base hasn't changed, the type 04 is redundant — data is still
                // continuous with the current segment (some compilers emit periodic
                // type 04 records without actually changing the address). Keep merging.
                continue;
            }

            if (recordType != 0x00)  // skip unknown types
                continue;

            // Data record — flush if non-contiguous
            var absAddr = baseAddress + address;
            if (currentAddr.HasValue && absAddr != currentAddr.Value + (uint)currentData.Count)
            {
                // Check for 16-bit address wrap: when data crosses a 64KB page boundary
                // without a type 04 increment (or with a redundant same-base type 04),
                // the record address wraps from 0xFFFF back to 0x0000, making absAddr
                // appear to jump back by exactly 0x10000. This data is still continuous.
                uint expectedNext = currentAddr.Value + (uint)currentData.Count;
                if (expectedNext > absAddr && (expectedNext - absAddr) % 0x10000 == 0)
                {
                    // Address wrap — data is continuous, keep accumulating.
                }
                else
                {
                    FlushSegment(segments, currentAddr, currentData);
                    currentAddr = null;  // force reset so next block sets new address
                }
            }

            if (!currentAddr.HasValue)
                currentAddr = absAddr;

            for (int i = 0; i < byteCount; i++)
                currentData.Add(ParseHexByte(line, 9 + i * 2));
        }

        FlushSegment(segments, currentAddr, currentData);
        return new FirmwareFile(path, FirmwareFormat.IntelHex, segments);
    }

    /// <summary>
    /// Parse a Motorola S-record (S19) file. Handles S1 (2-byte addr),
    /// S2 (3-byte), S3 (4-byte) data records and S9/S8/S7 EOF.
    /// </summary>
    public static FirmwareFile ParseMotorolaS19(string path)
    {
        var lines = System.IO.File.ReadAllLines(path);
        var segments = new List<Segment>();
        uint? currentAddr = null;
        var currentData = new List<byte>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != 'S')
                continue;

            // Phase 2 S1: Validate checksum before parsing.
            ValidateS19Checksum(line);

            var type = line[1];
            if (type == '9' || type == '8' || type == '7')  // EOF
                break;
            if (type != '1' && type != '2' && type != '3')
                continue;

            var byteCount = ParseHexByte(line, 2);
            var addrLen = type - '0' + 1;  // S1=2, S2=3, S3=4
            uint address = 0;
            for (int i = 0; i < addrLen; i++)
                address = (address << 8) | ParseHexByte(line, 4 + i * 2);

            var dataBytes = byteCount - addrLen - 1;  // subtract addr + checksum
            if (currentAddr.HasValue && address != currentAddr.Value + (uint)currentData.Count)
                FlushSegment(segments, currentAddr, currentData);

            if (!currentAddr.HasValue)
                currentAddr = address;

            for (int i = 0; i < dataBytes; i++)
                currentData.Add(ParseHexByte(line, 4 + addrLen * 2 + i * 2));
        }

        FlushSegment(segments, currentAddr, currentData);
        return new FirmwareFile(path, FirmwareFormat.MotorolaS19, segments);
    }

    /// <summary>
    /// Parse a raw binary file — single segment at address 0 (operator must specify).
    /// </summary>
    public static FirmwareFile ParseRawBinary(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        var segment = new Segment(0, bytes) { Crc32 = Crc32.Compute(bytes) };
        return new FirmwareFile(path, FirmwareFormat.RawBinary, new[] { segment });
    }

    private static void FlushSegment(List<Segment> segments, uint? address, List<byte> data)
    {
        if (address.HasValue && data.Count > 0)
        {
            var bytes = data.ToArray();
            segments.Add(new Segment(address.Value, bytes) { Crc32 = Crc32.Compute(bytes) });
            data.Clear();
        }
    }

    private static byte ParseHexByte(string line, int start) =>
        byte.Parse(line.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// Phase 2 S1: Validate Intel HEX record checksum. The checksum is the two's
    /// complement of the sum of all bytes (byte count + address + type + data + checksum).
    /// A valid record sums to 0 (mod 256).
    /// </summary>
    private static void ValidateIntelHexChecksum(string line)
    {
        // Format: :LLAAAATT[DD...]CC — colon + 2 hex chars per byte
        if (line.Length < 5 || (line.Length - 1) % 2 != 0)
            throw new FormatException($"Invalid Intel HEX line length: {line}");

        byte sum = 0;
        for (int i = 1; i < line.Length; i += 2)
            sum += ParseHexByte(line, i);

        if (sum != 0)
            throw new FormatException($"Intel HEX checksum mismatch in line: {line}");
    }

    /// <summary>
    /// Phase 2 S1: Validate Motorola S-record checksum. The checksum is the one's
    /// complement of the sum of all bytes (byte count + address + data + checksum),
    /// excluding the record type character. A valid record sums to 0xFF.
    /// </summary>
    private static void ValidateS19Checksum(string line)
    {
        // Format: S1 LL AAAA [DD...] CC — S + type + 2 hex chars per byte
        if (line.Length < 6 || (line.Length - 2) % 2 != 0)
            throw new FormatException($"Invalid S-record line length: {line}");

        byte sum = 0;
        // Start at index 2 (skip 'S' and type character)
        for (int i = 2; i < line.Length; i += 2)
            sum += ParseHexByte(line, i);

        if (sum != 0xFF)
            throw new FormatException($"S-record checksum mismatch in line: {line}");
    }
}
