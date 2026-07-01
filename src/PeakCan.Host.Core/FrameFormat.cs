namespace PeakCan.Host.Core;

/// <summary>
/// CAN 2.0/CAN FD identifier width.
/// </summary>
public enum FrameFormat : byte
{
    Standard = 0, // 11-bit ID (CAN 2.0A)
    Extended = 1, // 29-bit ID (CAN 2.0B)
}