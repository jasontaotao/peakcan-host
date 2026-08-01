namespace PeakCan.Host.Core.HIL;

public sealed record HilRunRequest(
    string DbcPath,
    string SuitePath,
    string? TracePath = null,
    string? HardwareChannel = null,
    string Format = "console",
    uint UdsRequestId = 0x7DF,
    uint UdsResponseId = 0x7E8,
    // Phase 3 additions:
    string? EcuScriptPath = null,
    string? MatrixPath = null,
    bool EnableFaultInjection = false,
    // Sprint 12 additions:
    HilMode Mode = HilMode.TraceReplay,
    bool EnableAnalyze = false,
    // Phase 7 Unit B: external generator plugin directory
    string? GeneratorDir = null);
