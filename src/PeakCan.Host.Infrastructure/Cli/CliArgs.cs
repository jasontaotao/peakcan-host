using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Infrastructure.Cli;

/// <summary>
/// Parsed CLI arguments for peakcan-hil.
/// </summary>
public sealed record CliArgs(
    string DbcPath,
    string SuitePath,
    string? TracePath = null,
    string? OutputPath = null,
    string Format = "console",
    // Stage B additions:
    string? HardwareChannel = null,  // e.g. "USB1" — if set, use real hardware
    uint UdsRequestId = 0x7DF,
    uint UdsResponseId = 0x7E8,
    // Phase 3 Sprint 4 additions:
    string? EcuScriptPath = null,  // ECU simulator script JSON path
    // Phase 3 Sprint 5 additions:
    bool EnableFaultInjection = false,  // Enable fault injection in channel
    // Phase 3 Sprint 6 additions:
    string? MatrixPath = null,  // Multi-ECU matrix config JSON path
    // Phase 4 Sprint 8 additions (ODX import):
    string? ImportOdxPath = null,
    string? ImportOdxEcuName = null,
    uint ImportOdxRequestId = 0x7E0,
    uint ImportOdxResponseId = 0x7E8,
    // Phase 5 Sprint 13 additions (standalone simulator):
    bool Simulate = false,
    // Phase 6 Sprint 15 additions (report format + frame export):
    string? ExportFramesDir = null,
    // Phase 7 Unit B additions (external generator plugin directory):
    string? GeneratorDir = null,
    // Phase 7 Unit D additions (multi-bus gateway config):
    string? GatewayPath = null,
    // 2026-08-22: 多通道硬件声明（spec §3.4）。非空 = 多通道模式（每通道独立 handle/DBC/FD）；
    // null = 旧单通道 HardwareChannel 路径。CLI 不直接解析（多通道主要走 WPF HilRunRequest 路径）。
    IReadOnlyList<ChannelConfig>? HardwareChannels = null,
    // 2026-09-07 backlog §9 1.7.6：seed-key 算法 DLL（OEM GenerateKey cdecl 导出）。
    // 非空 = DllKeyDerivationAlgorithm 挂进 UdsClient（SecurityAccess 步骤可用）；
    // null = 无算法（SecurityAccess fail-fast KeyAlgorithmNotConfiguredException）。
    string? KeyDllPath = null,
    // SecOC Phase 2 (spec D4)：密钥管理命令模式。非空 = key 管理模式（import/list/remove），
    // 早退于常规 run 流程；null = 常规 HIL run。
    string? SecOcKeyCommand = null,
    string? SecOcKeyId = null,
    string? SecOcKeyPath = null,
    string? SecOcStoreDir = null,
    string? SecOcEntropy = null,
    // SecOC Phase 2：headless 运行时的 PDU 配置（D4：keyId 引用，缺失即启动拦截）
    string? SecOcConfigPath = null,
    // M3.4（spec 2026-09-07 Phase 3）：--key-algorithm 选择入口。当前唯一取值 builtin
    // = 内置 XOR-0xAA（与虚拟 ECU seed 算法一致，无需 DLL）；与 KeyDllPath 互斥，
    // KeyDllPath 优先。两者皆空 = 占位算法（SecurityAccess fail-fast）。
    string? KeyAlgorithm = null);

/// <summary>
/// Simple CLI argument parser for peakcan-hil.
/// </summary>
public static class CliArgsParser
{
    public static CliArgs Parse(string[] args)
    {
        string? dbc = null, trace = null, suite = null, output = null, format = "console";
        string? hw = null, ecu = null, matrix = null;
        bool enableFaults = false;
        uint udsReq = 0x7DF, udsResp = 0x7E8;
        // Phase 4 ODX import
        string? importOdx = null, importEcuName = null;
        uint importReq = 0x7E0, importResp = 0x7E8;
        // Phase 5 Sprint 13 standalone simulator
        bool simulate = false;
        // Phase 6 Sprint 15 frame export directory
        string? exportFramesDir = null;
        // Phase 7 Unit B external generator plugin directory
        string? generatorDir = null;
        // Phase 7 Unit D multi-bus gateway config
        string? gatewayPath = null;
        // 2026-09-07 backlog §9 1.7.6 seed-key 算法 DLL
        string? keyDll = null;
        string? keyAlgorithm = null;
        // SecOC Phase 2 key management
        string? secocKeyCommand = null, secocKeyId = null, secocKeyPath = null;
        string? secocStoreDir = null, secocEntropy = null;
        // SecOC Phase 2 headless PDU config
        string? secocConfigPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--secoc-key": secocKeyCommand = NextArg(args, ref i, "--secoc-key"); break;
                case "--key-id": secocKeyId = NextArg(args, ref i, "--key-id"); break;
                case "--key-file": secocKeyPath = NextArg(args, ref i, "--key-file"); break;
                case "--store-dir": secocStoreDir = NextArg(args, ref i, "--store-dir"); break;
                case "--entropy": secocEntropy = NextArg(args, ref i, "--entropy"); break;
                case "--secoc-config": secocConfigPath = NextArg(args, ref i, "--secoc-config"); break;
                case "--dbc": dbc = NextArg(args, ref i, "--dbc"); break;
                case "--trace": trace = NextArg(args, ref i, "--trace"); break;
                case "--suite": suite = NextArg(args, ref i, "--suite"); break;
                case "--output": output = NextArg(args, ref i, "--output"); break;
                case "--format": format = NextArg(args, ref i, "--format"); break;
                case "--hw": hw = NextArg(args, ref i, "--hw"); break;
                case "--ecu": ecu = NextArg(args, ref i, "--ecu"); break;
                case "--matrix": matrix = NextArg(args, ref i, "--matrix"); break;
                case "--enable-faults": enableFaults = true; break;
                case "--uds-req": udsReq = ParseUdsId(NextArg(args, ref i, "--uds-req"), "--uds-req"); break;
                case "--uds-resp": udsResp = ParseUdsId(NextArg(args, ref i, "--uds-resp"), "--uds-resp"); break;
                case "--import-odx": importOdx = NextArg(args, ref i, "--import-odx"); break;
                case "--ecu-name": importEcuName = NextArg(args, ref i, "--ecu-name"); break;
                case "--import-uds-req": importReq = ParseUdsId(NextArg(args, ref i, "--import-uds-req"), "--import-uds-req"); break;
                case "--import-uds-resp": importResp = ParseUdsId(NextArg(args, ref i, "--import-uds-resp"), "--import-uds-resp"); break;
                case "--simulate": simulate = true; break;
                case "--export-frames": exportFramesDir = NextArg(args, ref i, "--export-frames"); break;
                case "--generator-dir": generatorDir = NextArg(args, ref i, "--generator-dir"); break;
                case "--gateway": gatewayPath = NextArg(args, ref i, "--gateway"); break;
                case "--key-dll": keyDll = NextArg(args, ref i, "--key-dll"); break;
                // M3.4：内置算法选择（当前仅 builtin = XOR-0xAA）；与 --key-dll 互斥，DLL 优先
                case "--key-algorithm": keyAlgorithm = NextArg(args, ref i, "--key-algorithm"); break;
                case "--help":
                case "-h":
                    PrintHelp();
                    System.Environment.Exit(0);
                    break;
            }
        }

        var allowedFormats = new[] { "console", "trx", "junit", "html", "html+junit", "json" };
        if (!allowedFormats.Contains(format))
            throw new ArgumentException($"Unsupported --format '{format}'. Expected: {string.Join(", ", allowedFormats)}.");

        // SecOc key management mode: standalone, no --dbc/--suite required
        if (secocKeyCommand is not null)
        {
            return new CliArgs(dbc ?? "", suite ?? "", SecOcKeyCommand: secocKeyCommand,
                SecOcKeyId: secocKeyId, SecOcKeyPath: secocKeyPath,
                SecOcStoreDir: secocStoreDir, SecOcEntropy: secocEntropy);
        }

        // Validation: ODX import mode OR simulate mode OR normal mode
        if (importOdx is not null)
        {
            // ODX import mode: no other required args
            return new CliArgs(dbc ?? "", suite ?? "", trace, output, format, hw, udsReq, udsResp,
                ecu, enableFaults, matrix, importOdx, importEcuName, importReq, importResp, Simulate: false, exportFramesDir, GeneratorDir: generatorDir, GatewayPath: gatewayPath, KeyDllPath: keyDll, KeyAlgorithm: keyAlgorithm,
                SecOcConfigPath: secocConfigPath, SecOcStoreDir: secocStoreDir, SecOcEntropy: secocEntropy);
        }

        if (simulate)
        {
            // Standalone simulator mode: requires --ecu and --hw
            if (ecu is null)
                throw new ArgumentException("--simulate requires --ecu <path>.");
            if (hw is null)
                throw new ArgumentException("--simulate requires --hw <channel>.");
            if (dbc is null)
                throw new ArgumentException("--simulate requires --dbc <path>.");
            return new CliArgs(dbc, suite ?? "", trace, output, format, hw, udsReq, udsResp,
                ecu, enableFaults, matrix, null, null, importReq, importResp, Simulate: true, exportFramesDir, GeneratorDir: generatorDir, GatewayPath: gatewayPath, KeyDllPath: keyDll, KeyAlgorithm: keyAlgorithm,
                SecOcConfigPath: secocConfigPath, SecOcStoreDir: secocStoreDir, SecOcEntropy: secocEntropy);
        }

        if (dbc is null) throw new ArgumentException("Missing required --dbc argument.");
        if (suite is null) throw new ArgumentException("Missing required --suite argument.");
        if (trace is null && hw is null && ecu is null && matrix is null)
            throw new ArgumentException("Must specify --trace, --hw, --ecu, or --matrix.");
        if (trace is not null && hw is not null)
            throw new ArgumentException("Cannot use --trace and --hw simultaneously.");
        if (ecu is not null && hw is not null)
            throw new ArgumentException("Cannot use --ecu and --hw simultaneously.");
        if (matrix is not null && hw is not null)
            throw new ArgumentException("Cannot use --matrix and --hw simultaneously.");
        if (matrix is not null && ecu is not null)
            throw new ArgumentException("Cannot use --matrix and --ecu simultaneously.");

        return new CliArgs(dbc, suite, trace, output, format, hw, udsReq, udsResp, ecu, enableFaults, matrix,
            importOdx, importEcuName, importReq, importResp, Simulate: false, exportFramesDir, GeneratorDir: generatorDir, GatewayPath: gatewayPath, KeyDllPath: keyDll, KeyAlgorithm: keyAlgorithm,
            SecOcConfigPath: secocConfigPath, SecOcStoreDir: secocStoreDir, SecOcEntropy: secocEntropy);
    }

    /// <summary>
    /// Read and consume the value that follows <paramref name="option"/>.
    /// </summary>
    private static string NextArg(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {option}.");
        i++;
        return args[i];
    }

    /// <summary>
    /// 解析 UDS CAN ID 字符串（支持十进制和 0x 前缀十六进制）。
    /// </summary>
    private static uint ParseUdsId(string raw, string option)
    {
        var isHex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var number = isHex ? raw[2..] : raw;
        if (!uint.TryParse(number,
                isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id))
        {
            throw new ArgumentException($"Invalid CAN ID for {option}: '{raw}'.");
        }
        return id;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: peakcan-hil --dbc <path.dbc> --trace <path.asc|path.blf> --suite <tests.json> [options]");
        Console.WriteLine("       peakcan-hil --dbc <path.dbc> --hw USB1 --suite <tests.json> [options]");
        Console.WriteLine("       peakcan-hil --dbc <path.dbc> --ecu <script.json> --hw USB1 --simulate");
        Console.WriteLine("       peakcan-hil --import-odx <path.odx> --ecu-name <name> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output <path>    Output file path (TRX or JUnit XML, or HTML report)");
        Console.WriteLine("  --format <format>  Output format: console (default), trx, junit, html, html+junit, json");
        Console.WriteLine("  --export-frames <dir>  Export fault frames as .asc files (independent of format)");
        Console.WriteLine("  --hw <channel>    Hardware channel (USB1..USB16) for real PCAN");
        Console.WriteLine("  --ecu <path>      ECU simulator script JSON path");
        Console.WriteLine("  --simulate        Standalone ECU simulator mode (requires --ecu and --hw)");
        Console.WriteLine("  --uds-req <id>    UDS request CAN ID (default: 0x7DF)");
        Console.WriteLine("  --uds-resp <id>   UDS response CAN ID (default: 0x7E8)");
        Console.WriteLine("  --import-odx <path>  Import ODX file and generate ECU script JSON");
        Console.WriteLine("  --ecu-name <name>    ECU name for ODX import (default: ImportedECU)");
        Console.WriteLine("  --import-uds-req <id>   Request CAN ID for ODX import (default: 0x7E0)");
        Console.WriteLine("  --import-uds-resp <id>  Response CAN ID for ODX import (default: 0x7E8)");
        Console.WriteLine("  --generator-dir <path>  Directory of external IEcuResponseGenerator plugin DLLs");
        Console.WriteLine("  --gateway <path>  Multi-bus gateway config JSON (bus-to-bus frame forwarding)");
        Console.WriteLine("  --key-dll <path>  OEM seed-key DLL (cdecl GenerateKey(seed, seedLen, keyOut, keyOutLen, securityLevel)) for SecurityAccess steps");
        Console.WriteLine("  --key-algorithm builtin  Built-in XOR-0xAA seed-key (no DLL; matches virtual ECU); ignored when --key-dll present");
        Console.WriteLine();
        Console.WriteLine("SecOc key management (spec D4):");
        Console.WriteLine("  --secoc-key <cmd>   import | list | remove (standalone mode, no --dbc/--suite)");
        Console.WriteLine("  --key-id <id>       KeyStore key identifier");
        Console.WriteLine("  --key-file <path>   128-bit hex key file (whitespace tolerated), import only");
        Console.WriteLine("  --store-dir <path>  KeyStore directory (default: %LOCALAPPDATA%\\PeakCan\\SecOc\\KeyStore)");
        Console.WriteLine("  --entropy <string>  Optional DPAPI additional entropy");
        Console.WriteLine("  --secoc-config <path>  SecOC PDU config JSON for headless runs (keyId refs, D4)");
        Console.WriteLine("  --help, -h         Show this help");
    }
}



