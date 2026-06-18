namespace PeakCan.Host.Core;

/// <summary>
/// CAN frame payload type. Maps to PCAN-Basic <c>PCAN_MESSAGE_TYPE</c>.
/// </summary>
public enum FrameType : byte
{
    Data = 0,    // Normal data frame (CAN / CAN FD).
    Remote = 1,  // RTR — remote transmission request (CAN 2.0 only).
    Error = 2,   // Bus error frame (PCAN_ERROR_*).
    Status = 3,  // Channel status change (PCAN_STATUS_*).
}