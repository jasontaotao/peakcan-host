using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using PeakCan.HIL.Core.HIL.Security;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Security.Keystore;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// Loads SecOC PDU configuration for headless runs.
/// <para>
/// Phase 2 surface: raw config JSON (<c>--secoc-config</c>).
/// Phase 4 surface: the suite-embedded <see cref="SecOcBlock"/> authored by the studio
/// (spec §5-D3/§5-D4). Key material is always resolved from the local KeyStore by keyId
/// reference — a missing key fails startup loudly instead of silently bypassing verification.
/// </para>
/// </summary>
public static class SecOcConfigLoader
{
    private static readonly JsonSerializerOptions s_pduEntryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

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
            s_pduEntryJsonOptions)
            ?? throw new InvalidOperationException($"SecOC config '{configPath}' is empty.");
        if (entries.Count == 0)
            throw new InvalidOperationException(
                $"SecOC config '{configPath}' declares no PDUs; remove --secoc-config instead of silently running unprotected.");

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
            var dataId = ParseNumber(entry.DataId, nameof(entry.DataId));
            if (dataId > ushort.MaxValue)
                throw new InvalidOperationException($"SecOC config: DataId '{entry.DataId}' exceeds 16 bits.");
            // Key ownership transfers to SecOcPduConfig; SecOcChannel.BuildRuntime takes
            // its own defensive clone for the authenticator. No zeroing here.
            result[canId] = new SecOcPduConfig
            {
                Profile = new SecOcProfile
                {
                    DataId = (ushort)dataId,
                    FvLenBits = entry.FvLenBits,
                    MacLenBits = entry.MacLenBits,
                },
                Key = keyStore.GetKey(entry.KeyId),
                Mode = ParseMode(entry.Mode),
                InitialFv = entry.InitialFv,
            };
        }
        return result;
    }

    /// <summary>
    /// Phase 4：从 suite 内嵌的 <see cref="SecOcBlock"/> 构建 PDU 配置（spec §8 Phase 4）。
    /// 结构先经 <see cref="SecOcBlockValidator"/> 校验；再逐个按 keyId 从 KeyStore 取密钥，
    /// 缺失即抛（D4 fail-loud）。字典按 CAN <c>Raw</c> 键控，与 <see cref="SecOcChannel"/> 查表口径一致。
    /// </summary>
    public static IReadOnlyDictionary<uint, SecOcPduConfig> LoadFromBlock(SecOcBlock block, IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(keyStore);

        var errors = SecOcBlockValidator.Validate(block);
        if (errors.Count > 0)
            throw new InvalidOperationException("SecOC suite block is invalid: " + string.Join(" ", errors));

        var result = new Dictionary<uint, SecOcPduConfig>();
        foreach (var pdu in block.Pdus!)
        {
            if (!keyStore.Contains(pdu.KeyId))
                throw new InvalidOperationException(
                    $"SecOC suite block: keyId '{pdu.KeyId}' not found in KeyStore " +
                    "(import it via `peakcan-hil --secoc-key import` before running).");

            result[pdu.CanId.Raw] = new SecOcPduConfig
            {
                Profile = new SecOcProfile
                {
                    DataId = pdu.DataId,
                    FvLenBits = pdu.FvLenBits,
                    MacLenBits = pdu.MacLenBits,
                },
                Key = keyStore.GetKey(pdu.KeyId),
                Mode = ParseMode(pdu.Mode),
                InitialFv = pdu.InitialFv,
            };
        }
        return result;
    }

    /// <summary>Phase 4：从 suite 块构建，密钥由本机 DPAPI KeyStore 解析（headless 默认路径）。</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<uint, SecOcPduConfig> LoadFromBlock(
        SecOcBlock block, string? storeDir = null, string? entropy = null)
        => LoadFromBlock(block, new DpapiKeyStore(storeDir ?? SecOcKeyCommand.DefaultStoreDir, entropy));

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
