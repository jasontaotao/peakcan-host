namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// DBC signal byte-order encoding. Maps to the @1 / @0 marker on a
/// <c>SG_</c> definition: <c>@1</c> = Intel / little-endian,
/// <c>@0</c> = Motorola / big-endian.
/// </summary>
public enum ByteOrder : byte
{
    BigEndian = 0,
    LittleEndian = 1,
}
