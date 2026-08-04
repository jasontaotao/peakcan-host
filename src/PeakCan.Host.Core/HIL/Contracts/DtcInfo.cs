namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// Parsed DTC entry (ISO 14229-1 §11.3.5).
/// Code is 2-byte (Motorola high byte first from 3-byte DTC field).
/// Status byte: bit 0 = testFailed, bit 2 = confirmedDTC.
/// </summary>
public sealed record DtcInfo(ushort Code, byte Status);
