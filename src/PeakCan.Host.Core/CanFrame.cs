namespace PeakCan.Host.Core;

/// <summary>
/// Immutable record representing one received or transmitted CAN/CAN-FD frame.
/// <para>
/// Carries enough metadata for the trace view, the DBC decoder, and the
/// statistics collector without needing to round-trip through PCAN-Basic types.
/// </para>
/// </summary>
/// <param name="Id">Frame identifier (Standard 11-bit or Extended 29-bit).</param>
/// <param name="Data">Payload bytes. Empty for Remote / Error / Status frames.</param>
/// <param name="Flags">Bit-level frame properties (RTR / FD / BRS / ESI / Err).</param>
/// <param name="Channel">Source channel handle (PCAN_USBxx etc.).</param>
/// <param name="Timestamp">Receive timestamp (microsecond monotonic counter).</param>
public readonly record struct CanFrame(
    CanId Id,
    ReadOnlyMemory<byte> Data,
    FrameFlags Flags,
    ChannelId Channel,
    Timestamp Timestamp)
{
    /// <summary>Payload length in bytes (= DLC for classical CAN; valid 0–8 or 0–64 for CAN FD).</summary>
    public byte Dlc => (byte)Data.Length;

    /// <summary>True iff the FD flag is set (CAN FD format, up to 64-byte payloads).</summary>
    public bool IsFd => (Flags & FrameFlags.Fd) != 0;

    /// <summary>True iff this is a hardware-reported bus error frame (not a data frame).</summary>
    public bool IsError => (Flags & FrameFlags.ErrFrame) != 0;

    // M8 fix: the synthesized Equals for record struct uses
    // EqualityComparer<ReadOnlyMemory<byte>>.Default, which compares
    // the underlying array reference, offset, and length — NOT the
    // byte content. Two CanFrames with identical byte content from
    // different array instances will not be equal. Override to compare
    // the actual span content.

    public bool Equals(CanFrame other)
        => Id == other.Id
           && Flags == other.Flags
           && Channel == other.Channel
           && Timestamp == other.Timestamp
           && Data.Span.SequenceEqual(other.Data.Span);

    public override int GetHashCode()
    {
        // Hash the metadata fields; hashing the full byte content on
        // every call is too expensive for the hot path (frame dispatch).
        // Collisions are fine — Equals catches them.
        return HashCode.Combine(Id, Flags, Channel, Timestamp);
    }
}