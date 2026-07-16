namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.51.0 MINOR: BLF (Vector Binary Logging Format) format single source.
/// All values verified 1:1 against vblf reference (zariiii9003/vblf master
/// branch, fetched 2026-07-16 to .superpowers/sdd/reference/). Per W22
/// LESSON: do not invent; if a constant appears wrong, re-verify against
/// reference before commit. Sister of v3.49.0 MINOR AscFormat.
/// </summary>
public static class BlfFormat
{
    /// <summary>BLF file signature: 4 ASCII bytes "LOGG" at file offset 0.
    /// Per vblf_constants.py line 4.</summary>
    public const string FileSignature = "LOGG";

    /// <summary>BLF object signature: 4 ASCII bytes "LOBJ" preceding each
    /// object in the file. Per vblf_constants.py line 5.</summary>
    public const string ObjSignature = "LOBJ";

    /// <summary>Object header base size in bytes. The vblf ObjectHeaderBase
    /// layout is 4-byte LOBJ signature + 2-byte header size + 2-byte header
    /// version + 4-byte object size + 4-byte object type = 16 bytes. The
    /// versioned ObjectHeader adds 16 bytes (IHHQ), for 32 bytes on disk.</summary>
    public const int ObjectHeaderBaseSize = 16;

    /// <summary>Complete versioned object header size in bytes: 16-byte
    /// ObjectHeaderBase plus 16-byte ObjectHeader extension (IHHQ), matching
    /// vblf_general.py ObjectHeader.SIZE.</summary>
    public const int ObjectHeaderSize = 32;

    /// <summary>File statistics header size in bytes, including the LOGG
    /// signature as its first 4 bytes. The vblf FileStatistics layout is
    /// 4sIIBBBBQQII32xQ64s = 144 bytes.</summary>
    public const int FileHeaderSize = 144;

    // Object type IDs — per vblf_constants.py lines 11, 20, 96, 110-111.
    public const uint ObjTypeCanMessage = 1;        // classic CAN 11-bit
    public const uint ObjTypeLogContainer = 10;     // zlib-compressed container
    public const uint ObjTypeCanMessage2 = 86;      // classic CAN 29-bit
    public const uint ObjTypeCanFdMessage = 100;    // CAN FD
    public const uint ObjTypeCanFdMessage64 = 101;  // CAN FD 64-byte

    // Frame data format sizes — per vblf_can.py struct.Struct("...").size.
    public const int CanMessageDataSize = 12;        // HBBI8s
    public const int CanMessage2DataSize = 28;       // HBBI8sIBBH
    public const int CanFdMessageDataSize = 76;      // HBBIIBBBBI64sI
    public const int CanFdMessage64DataSize = 48;    // BBBBIIIIIIIHBBI
    public const int CanFdMessage64ExtSize = 8;      // II (extension)

    /// <summary>Timestamp scale: vblf stores as 10ns ticks since Vector
    /// epoch; divide by this to get seconds.</summary>
    public const double TimestampScale = 10_000_000.0;
}