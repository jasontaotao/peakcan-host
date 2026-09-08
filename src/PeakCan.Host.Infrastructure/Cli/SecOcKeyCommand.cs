using System.Runtime.Versioning;
using System.Security.Cryptography;
using PeakCan.Security.Keystore;

namespace PeakCan.Host.Infrastructure.Cli;

/// <summary>
/// SecOc key management CLI command (spec D4, Phase 2 hard prerequisite).
/// Imports 128-bit AES keys into the local KeyStore; keys never live in
/// suite JSON / git. Exit codes: 0 = ok, 1 = not found, 2 = usage/input error.
/// </summary>
public static class SecOcKeyCommand
{
    /// <summary>Default on-disk store location: %LOCALAPPDATA%\PeakCan\SecOc\KeyStore.</summary>
    public static string DefaultStoreDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan", "SecOc", "KeyStore");

    /// <summary>Real store factory (DPAPI CurrentUser, Windows only). Tests may substitute.</summary>
    [SupportedOSPlatform("windows")]
    public static IKeyStore DefaultStoreFactory(string storeDir, string? entropy) => new DpapiKeyStore(storeDir, entropy);

    private const int AesKeyLength = 16;

    public static int Run(CliArgs args, Func<string, string?, IKeyStore>? storeFactory = null,
        TextWriter? output = null, TextWriter? error = null)
    {
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;
        var storeDir = args.SecOcStoreDir ?? DefaultStoreDir;
        var factory = storeFactory ?? DefaultStoreFactory;

        switch (args.SecOcKeyCommand)
        {
            case "import":
                return RunImport(args, storeDir, factory, stdout, stderr);
            case "list":
                return RunList(storeDir, args.SecOcEntropy, factory, stdout);
            case "remove":
                return RunRemove(args, storeDir, factory, stdout, stderr);
            default:
                stderr.WriteLine($"Unknown SecOc key command '{args.SecOcKeyCommand}'. Expected: import, list, remove.");
                return 2;
        }
    }

    private static int RunImport(CliArgs args, string storeDir,
        Func<string, string?, IKeyStore> factory, TextWriter stdout, TextWriter stderr)
    {
        if (string.IsNullOrWhiteSpace(args.SecOcKeyId))
        {
            stderr.WriteLine("--secoc-key import requires --key-id <id>.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(args.SecOcKeyPath))
        {
            stderr.WriteLine("--secoc-key import requires --key-file <path> (hex text).");
            return 2;
        }

        byte[] key;
        try
        {
            key = ReadKeyFile(args.SecOcKeyPath!);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or FormatException)
        {
            stderr.WriteLine($"Cannot read key file '{args.SecOcKeyPath}': {ex.Message}");
            return 2;
        }

        if (key.Length != AesKeyLength)
        {
            CryptographicOperations.ZeroMemory(key);
            stderr.WriteLine($"Key must be {AesKeyLength} bytes (AES-128), got {key.Length}.");
            return 2;
        }

        try
        {
            factory(storeDir, args.SecOcEntropy).SetKey(args.SecOcKeyId!, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        stdout.WriteLine($"Imported key '{args.SecOcKeyId}' ({AesKeyLength} bytes) into {storeDir}");
        return 0;
    }

    private static int RunList(string storeDir, string? entropy,
        Func<string, string?, IKeyStore> factory, TextWriter stdout)
    {
        var ids = factory(storeDir, entropy).KeyIds.ToArray();
        if (ids.Length == 0)
        {
            stdout.WriteLine($"(empty) {storeDir}");
            return 0;
        }
        foreach (var id in ids)
            stdout.WriteLine(id);
        return 0;
    }

    private static int RunRemove(CliArgs args, string storeDir,
        Func<string, string?, IKeyStore> factory, TextWriter stdout, TextWriter stderr)
    {
        if (string.IsNullOrWhiteSpace(args.SecOcKeyId))
        {
            stderr.WriteLine("--secoc-key remove requires --key-id <id>.");
            return 2;
        }

        if (!factory(storeDir, args.SecOcEntropy).RemoveKey(args.SecOcKeyId!))
        {
            stderr.WriteLine($"keyId '{args.SecOcKeyId}' not found in {storeDir}");
            return 1;
        }
        stdout.WriteLine($"Removed key '{args.SecOcKeyId}' from {storeDir}");
        return 0;
    }

    /// <summary>Reads a hex text key file; whitespace is tolerated, any other
    /// non-hex character is rejected loudly (a silently stripped stray character
    /// would import a different key than the user thinks).</summary>
    private static byte[] ReadKeyFile(string path)
    {
        var text = File.ReadAllText(path);
        var invalid = text.Where(c => !char.IsWhiteSpace(c) && !char.IsAsciiHexDigit(c)).ToList();
        if (invalid.Count > 0)
            throw new FormatException(
                $"key file contains {invalid.Count} non-hex character(s), e.g. '{invalid[0]}'.");
        var hex = string.Concat(text.Where(char.IsAsciiHexDigit));
        return Convert.FromHexString(hex);
    }
}
