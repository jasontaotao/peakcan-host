namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: BLF (Vector Binary Logging Format) format single source.
/// Sister of v3.49.0 MINOR AscFormat. Pure static constants — no state,
/// no DI. All values verified against Vector BLF specification (constants
/// must be copied 1:1 from reverse-engineered reference per W22 + W23
/// LESSON; do not invent).
/// </summary>
public static class BlfFormat
{
    /// <summary>BLF file header signature: 4 ASCII bytes "LOGG".</summary>
    public const string FileSignature = "LOGG";

    /// <summary>BLF format signature (follows FileSignature): 4 ASCII bytes "LBLF".</summary>
    public const string FormatSignature = "LBLF";

    /// <summary>Supported version 1.0 (major 0x01, minor 0x00 packed in UINT32 LE).</summary>
    public const uint Version10 = 0x00010000;

    /// <summary>Supported version 2.0.</summary>
    public const uint Version20 = 0x00020000;

    /// <summary>Object header signature: 4 ASCII bytes "OBJH" (32-byte OBJH precedes each obj).</summary>
    public const string ObjHeader = "OBJH";

    /// <summary>Frame data blob signature: 4 ASCII bytes "BLOB".</summary>
    public const string Blob = "BLOB";

    /// <summary>Container object signature: 4 ASCII bytes "LOBJ" (wraps child objects).</summary>
    public const string Container = "LOBJ";

    /// <summary>Frame container type ID for classic CAN 2.0 (11/29-bit ID, 8-byte payload).</summary>
    public const uint ET_CAN_DATA = 5;

    /// <summary>Frame container type ID for CAN FD (flexible data-rate, up to 64-byte payload).</summary>
    public const uint ET_CAN_FD_DATA = 29;

    /// <summary>Frame container type ID for CAN XL (extended length, up to 2048-byte payload).</summary>
    public const uint ET_CAN_XL_DATA = 33;

    /// <summary>Classic CAN BLOB layout size in bytes: 2 (channel) + 2 (flags) + 1 (dlc) + 3 (reserved) + 4 (id) + 8 (data).</summary>
    public const int ClassicCanBlobSize = 20;

    /// <summary>CAN FD BLOB header size in bytes (data[64] follows): 20 (classic layout) + 1 (frameLength) + 3 (reserved) + 8 (padding before data[64]).</summary>
    public const int CanFdBlobSize = 32;

    /// <summary>CAN XL BLOB header size in bytes (data[2048] follows): 20 (classic layout) + 2 (frameLength BIG-ENDIAN) + 2 (reserved) + 8 (padding before data[2048]).</summary>
    public const int CanXlBlobMinSize = 32;

    /// <summary>OBJH timestamp is UINT64 in 100-nanosecond ticks; divide by this to get seconds.</summary>
    public const double TimestampScale = 10_000_000.0;

    /// <summary>Frame flag bit: CAN FD frame.</summary>
    public const ushort FlagFd = 0x0001;

    /// <summary>Frame flag bit: bit rate switch (CAN FD BRS).</summary>
    public const ushort FlagBrs = 0x0002;

    /// <summary>Frame flag bit: error state indicator (CAN FD ESI).</summary>
    public const ushort FlagEsi = 0x0004;

    /// <summary>Frame flag bit: CAN XL frame.</summary>
    public const ushort FlagXl = 0x0010;
}