namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// One CAN message (frame) defined in a DBC file.
/// <para>
/// <see cref="Id"/> carries the merged IDE bit for extended frames
/// (bit 31 set), matching PEAK's <c>PCAN_MESSAGE_ID</c> convention.
/// Use <c>(id &amp; 0x80000000u) == 0</c> to test Standard / Extended.
/// </para>
/// </summary>
public sealed record Message(
    uint Id,
    string Name,
    byte Dlc,
    string Sender,
    IList<Signal> Signals,
    bool IsMultiplexed,
    ushort? MultiplexorSignalIndex);
