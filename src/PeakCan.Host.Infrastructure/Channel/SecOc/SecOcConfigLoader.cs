using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Security.Keystore;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// Loads SecOC PDU configuration for headless runs (Phase 2: raw config JSON —
/// the studio SecurityBlock authoring arrives in Phase 4, spec §5-D3).
/// Key material is resolved from the local KeyStore by keyId reference
/// (spec §5-D4); a missing key fails startup loudly instead of silently
/// bypassing verification.
/// </summary>
public static class SecOcConfigLoader
{
    public sealed record SecOcPduEntry
    {
        public string CanId { get; init; } = "";
        public string DataId { get; init; } = "";
        public int FvLenBits { get; init; } = 16;
        public int MacLenBits { get; init; } = 24;
        public string KeyId { get; init; } = "";
        public string Mode { get; init; } = "both";
        public uint InitialFv { get; init; }
    }

    /// <summary>Loads PDU configs from JSON, or null when no config path given.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<uint, SecOcPduConfig>? LoadOptional(
        string? configPath, string? storeDir = null, string? entropy = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return null;

        var entries = JsonSerializer.Deserialize<List<SecOcPduEntry>>(
            File.ReadAllText(configPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidOperationException($"SecOC config '{configPath}' is empty.");

        var keyStore = new DpapiKeyStore(storeDir ?? SecOcKeyCommand.DefaultStoreDir, entropy);
        var result = new Dictionary<uint, SecOcPduConfig>();
        foreach (var entry in entries)
        {
            var canId = ParseNumber(entry.CanId, nameof(entry.CanId));
            if (result.ContainsKey(canId))
                throw new InvalidOperationException($"SecOC config: duplicate CAN id 0x{canId:X}.");
            if (!keyStore.Contains(entry.KeyId))
                throw new InvalidOperationException(
                    $"SecOC config: keyId '{entry.KeyId}' not found in KeyStore " +
                    "(import it via `peakcan-hil --secoc-key import` before running).");
            var key = keyStore.GetKey(entry.KeyId);
            SecOcPduConfig pduConfig;
            try
            {
                pduConfig = new SecOcPduConfig
                {
                    Profile = new SecOcProfile
                    {
                        DataId = (ushort)ParseNumber(entry.DataId, nameof(entry.DataId)),
                        FvLenBits = entry.FvLenBits,
                        MacLenBits = entry.MacLenBits,
                    },
                    Key = key,
                    Mode = ParseMode(entry.Mode),
                    InitialFv = entry.InitialFv,
                };
            }
            finally
            {
                // SecOcPduConfig/SecOcAuthenticator hold their own defensive copies.
                CryptographicOperations.ZeroMemory(key);
            }
            result[canId] = pduConfig;
        }
        return result;
    }

    private static SecOcPduMode ParseMode(string mode) => mode.ToLowerInvariant() switch
    {
        "verify" => SecOcPduMode.Verify,
        "sign" => SecOcPduMode.Sign,
        "both" => SecOcPduMode.Both,
        "bypass" => SecOcPduMode.Bypass,
        _ => throw new InvalidOperationException($"SecOC config: unknown mode '{mode}'."),
    };

    private static uint ParseNumber(string raw, string field)
    {
        var text = raw.Trim();
        var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var ok = uint.TryParse(
            isHex ? text[2..] : text,
            isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value);
        if (!ok)
            throw new InvalidOperationException($"SecOC config: invalid {field} '{raw}'.");
        return value;
    }
}
