namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// Phase 2: Parsed firmware file — one file yields one or more address-contiguous
/// segments. HEX/S19 naturally produce multiple segments (e.g. bootloader @ 0x0800 +
/// app @ 0x10000). Raw binary yields a single segment whose address the operator must
/// specify. Each segment carries its own CRC32, computed at parse time.
/// </summary>
/// <param name="Path">Source file path.</param>
/// <param name="Format">Detected firmware format.</param>
/// <param name="Segments">Address-contiguous data segments extracted from the file.</param>
public sealed record FirmwareFile(
    string Path,
    FirmwareFormat Format,
    IReadOnlyList<Segment> Segments);

/// <summary>
/// One address-contiguous data region within a <see cref="FirmwareFile"/>. The
/// <see cref="StartAddress"/> is either embedded in the file (HEX/S19) or supplied by
/// the operator (raw binary). <see cref="Crc32"/> is auto-computed at parse time for
/// later Verify-step comparison.
/// </summary>
/// <param name="StartAddress">Target memory address (embedded or operator-specified).</param>
/// <param name="Data">Raw payload bytes for this segment.</param>
public sealed record Segment(
    uint StartAddress,
    byte[] Data)
{
    /// <remarks>
    /// byte[] is mutable — do not modify after construction. The <c>init</c> only
    /// protects the reference reassignment, not the array contents. If a future
    /// caller needs post-construction patch/overlay, copy-on-write or switch to
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> is required.
    /// </remarks>
    public uint Length => (uint)Data.Length;

    /// <summary>End address (inclusive). Uses <c>checked</c> to trap uint overflow.</summary>
    public uint EndAddress => checked(StartAddress + (uint)Data.Length - 1);

    /// <summary>Auto-computed CRC32 over <see cref="Data"/>. Set by the parser.</summary>
    public uint Crc32 { get; init; }
}

/// <summary>Firmware file format, auto-detected from extension + content.</summary>
public enum FirmwareFormat
{
    /// <summary>Raw binary — file bytes ARE the flash payload.</summary>
    RawBinary,

    /// <summary>Intel HEX — text records with embedded addresses.</summary>
    IntelHex,

    /// <summary>Motorola S-record (S19) — text records with embedded addresses.</summary>
    MotorolaS19,
}

/// <summary>
/// Phase 2: A flash driver — a small routine (DLL or raw binary) that gets downloaded to
/// ECU RAM and executed to perform the actual erase/write operations. Used when the ECU's
/// built-in bootloader doesn't support direct memory programming.
/// </summary>
/// <param name="Path">Source file path.</param>
/// <param name="Data">Raw driver bytes (the full blob downloaded to RAM).</param>
public sealed record FlashDriver(string Path, byte[] Data)
{
    public uint Length => (uint)Data.Length;
    public uint Checksum => Crc32.Compute(Data);

    /// <summary>
    /// Issue 3: 解析出的 Segment 列表 (从 Path 对应的 .hex/.s19 文件解析).
    /// 用于 Verify 步骤选择要校验的 Segment. 原始 HEX/S19 文件才有多 Segment,
    /// raw binary 只有单 Segment.
    /// </summary>
    public IReadOnlyList<Segment> Segments { get; init; } = [];
}
