namespace PeakCan.Host.Cli;

/// <summary>
/// Parsed CLI arguments for peakcan-hil.
/// </summary>
public sealed record CliArgs(
    string DbcPath,
    string TracePath,
    string SuitePath,
    string? OutputPath = null,
    string Format = "console");

/// <summary>
/// Simple CLI argument parser for peakcan-hil.
/// </summary>
public static class CliArgsParser
{
    public static CliArgs Parse(string[] args)
    {
        string? dbc = null, trace = null, suite = null, output = null, format = "console";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dbc": dbc = args[++i]; break;
                case "--trace": trace = args[++i]; break;
                case "--suite": suite = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--format": format = args[++i]; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        if (dbc is null) throw new ArgumentException("Missing required --dbc argument.");
        if (trace is null) throw new ArgumentException("Missing required --trace argument.");
        if (suite is null) throw new ArgumentException("Missing required --suite argument.");

        return new CliArgs(dbc, trace, suite, output, format);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: peakcan-hil --dbc <path.dbc> --trace <path.asc> --suite <tests.json> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output <path>    Output file path (TRX format)");
        Console.WriteLine("  --format <format>  Output format: console (default), trx");
        Console.WriteLine("  --help, -h         Show this help");
    }
}
