using System.Globalization;
using System.Text.Json;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Loads an ECU simulator script from JSON. Parses CAN IDs and response rules.
/// Swaps RequestId/ResponseId to produce ECU-perspective CanIdConfig.
/// </summary>
public static class EcuScriptLoader
{
    /// <summary>
    /// Load ECU script from a JSON file path.
    /// </summary>
    /// <param name="path">Absolute or relative path to the .json ECU script.</param>
    /// <returns>Parsed EcuScript with ECU-perspective CanIdConfig (IDs swapped).</returns>
    /// <exception cref="FileNotFoundException">Script file not found.</exception>
    /// <exception cref="JsonException">JSON malformed or missing required fields.</exception>
    public static EcuScript Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// Parse ECU script from JSON string.
    /// </summary>
    public static EcuScript Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Parse canIds (HIL perspective)
        var canIdsHil = root.GetProperty("canIds");
        var requestIdHil = ParseCanId(canIdsHil.GetProperty("requestId"));
        var responseIdHil = ParseCanId(canIdsHil.GetProperty("responseId"));
        var isExtended = canIdsHil.TryGetProperty("isExtendedFrame", out var ext) && ext.GetBoolean();

        // ECU side swaps RequestId/ResponseId
        var ecuCanIds = new CanIdConfig
        {
            RequestId = responseIdHil,   // ECU sends on HIL's ResponseId
            ResponseId = requestIdHil,   // ECU receives on HIL's RequestId
            IsExtendedFrame = isExtended
        };

        // Parse rules
        var rules = new List<UdsResponseRule>();
        foreach (var ruleEl in root.GetProperty("rules").EnumerateArray())
        {
            rules.Add(ParseRule(ruleEl));
        }

        return new EcuScript(
            Name: root.GetProperty("name").GetString()!,
            CanIds: ecuCanIds,
            Rules: rules);
    }

    private static uint ParseCanId(JsonElement element)
    {
        var s = element.GetString()!;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s[2..], NumberStyles.HexNumber)
            : uint.Parse(s);
    }

    private static byte ParseHexByte(string s)
    {
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.Parse(s[2..], NumberStyles.HexNumber)
            : byte.Parse(s);
    }

    private static UdsResponseRule ParseRule(JsonElement el)
    {
        var serviceId = ParseHexByte(el.GetProperty("serviceId").GetString()!);
        var responseData = el.GetProperty("responseData").EnumerateArray()
            .Select(b => b.GetByte()).ToArray();

        byte? subFunction = null;
        if (el.TryGetProperty("subFunction", out var subFunc) && subFunc.ValueKind != JsonValueKind.Null)
        {
            subFunction = subFunc.ValueKind == JsonValueKind.Number
                ? subFunc.GetByte()
                : ParseHexByte(subFunc.GetString()!);
        }

        byte[]? dataMask = null;
        byte[]? dataPattern = null;
        if (el.TryGetProperty("dataMask", out var mask))
        {
            dataMask = mask.EnumerateArray().Select(b => b.GetByte()).ToArray();
            dataPattern = el.GetProperty("dataPattern").EnumerateArray().Select(b => b.GetByte()).ToArray();
        }

        var delayMs = el.TryGetProperty("responseDelayMs", out var delay) ? delay.GetInt32() : 0;

        return new UdsResponseRule
        {
            ServiceId = serviceId,
            SubFunction = subFunction,
            DataMask = dataMask,
            DataPattern = dataPattern,
            ResponseData = responseData,
            ResponseDelayMs = delayMs
        };
    }
}
