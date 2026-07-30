namespace PeakCan.Host.Core.HIL;

public sealed record HilRunRequest(
    string DbcPath,
    string SuitePath,
    string? TracePath = null,
    string? HardwareChannel = null,
    string Format = "console",
    uint UdsRequestId = 0x7DF,
    uint UdsResponseId = 0x7E8);
