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
    string? ExportFramesDir = null);

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

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dbc": dbc = args[++i]; break;
                case "--trace": trace = args[++i]; break;
                case "--suite": suite = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--format": format = args[++i]; break;
                case "--hw": hw = args[++i]; break;
                case "--ecu": ecu = args[++i]; break;
                case "--matrix": matrix = args[++i]; break;
                case "--enable-faults": enableFaults = true; break;
                case "--uds-req": udsReq = ParseUdsId(args[++i]); break;
                case "--uds-resp": udsResp = ParseUdsId(args[++i]); break;
                // Phase 4 ODX import
                case "--import-odx": importOdx = args[++i]; break;
                case "--ecu-name": importEcuName = args[++i]; break;
                case "--import-uds-req": importReq = ParseUdsId(args[++i]); break;
                case "--import-uds-resp": importResp = ParseUdsId(args[++i]); break;
                // Phase 5 Sprint 13 standalone simulator
                case "--simulate": simulate = true; break;
                // Phase 6 Sprint 15 frame export
                case "--export-frames": exportFramesDir = args[++i]; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        // Validation: ODX import mode OR simulate mode OR normal mode
        if (importOdx is not null)
        {
            // ODX import mode: no other required args
            return new CliArgs(dbc ?? "", suite ?? "", trace, output, format, hw, udsReq, udsResp,
                ecu, enableFaults, matrix, importOdx, importEcuName, importReq, importResp, Simulate: false, exportFramesDir);
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
                ecu, enableFaults, matrix, null, null, importReq, importResp, Simulate: true, exportFramesDir);
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
            importOdx, importEcuName, importReq, importResp, Simulate: false, exportFramesDir);
    }

    /// <summary>
    /// 解析 UDS CAN ID 字符串（支持十进制和 0x 前缀十六进制）。
    /// </summary>
    private static uint ParseUdsId(string raw)
    {
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToUInt32(raw[2..], 16);
        return Convert.ToUInt32(raw);
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
        Console.WriteLine("  --format <format>  Output format: console (default), trx, junit, html, html+junit");
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
        Console.WriteLine("  --help, -h         Show this help");
    }
}
