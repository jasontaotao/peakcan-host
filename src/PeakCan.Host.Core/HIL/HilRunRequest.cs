namespace PeakCan.HIL.Core.HIL;

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
    string? GeneratorDir = null,
    // Test case selection: null = run all; non-empty = run only matching case names
    IReadOnlyList<string>? SelectedCaseNames = null,
    // 2026-08-15: WPF 每 case 全量报文 log
    bool CaptureCaseLogs = false,
    // 2026-08-22: 多通道硬件声明（spec §3.3/§3.4）。null = 旧单通道 HardwareChannel 路径不变。
    IReadOnlyList<ChannelConfig>? HardwareChannels = null,
    // 2026-08-15: 每 case 全量报文 log 目录（null = 默认 %LocalAppData%\PeakCanHost\hil-reports\case-logs\）
    string? CaseLogDirectory = null);
