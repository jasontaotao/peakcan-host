using System.Globalization;
using System.Text.Json;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Loads an ECU simulator script from JSON. Parses CAN IDs and states/rules.
/// Swaps RequestId/ResponseId to produce ECU-perspective CanIdConfig.
/// Supports both stateless ("rules") and stateful ("states") JSON formats.
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
        return ParseEcuScript(doc.RootElement);
    }

    /// <summary>
    /// Parse ECU script from a JSON element (used by both Parse and MatrixConfigLoader).
    /// </summary>
    public static EcuScript ParseEcuScript(JsonElement element)
    {
        // Parse canIds (HIL perspective) and swap to ECU perspective
        var canIds = ParseCanIds(element.GetProperty("canIds"));

        // Determine format: "states" (stateful) or "rules" (stateless)
        bool hasStates = element.TryGetProperty("states", out var statesEl);
        bool hasRules = element.TryGetProperty("rules", out var rulesEl);

        if (hasStates && hasRules)
            throw new JsonException("Cannot specify both 'states' and 'rules' in ECU script.");

        EcuStateMachine stateMachine;
        if (hasStates)
        {
            stateMachine = ParseStateMachine(statesEl, GetBuiltInGenerators());
        }
        else if (hasRules)
        {
            stateMachine = ParseRules(rulesEl);
        }
        else
        {
            throw new JsonException("ECU script must contain either 'states' or 'rules'.");
        }

        return new EcuScript(
            Name: element.GetProperty("name").GetString()!,
            CanIds: canIds,
            StateMachine: stateMachine);
    }

    /// <summary>
    /// Parse canIds from HIL perspective and swap to ECU perspective.
    /// ECU listens on HIL's RequestId, sends on HIL's ResponseId.
    /// </summary>
    private static CanIdConfig ParseCanIds(JsonElement canIdsEl)
    {
        var requestIdHil = ParseCanId(canIdsEl.GetProperty("requestId"));
        var responseIdHil = ParseCanId(canIdsEl.GetProperty("responseId"));
        var isExtended = canIdsEl.TryGetProperty("isExtendedFrame", out var ext) && ext.GetBoolean();

        return new CanIdConfig
        {
            RequestId = responseIdHil,   // ECU sends on HIL's ResponseId
            ResponseId = requestIdHil,   // ECU receives on HIL's RequestId
            IsExtendedFrame = isExtended
        };
    }

    /// <summary>
    /// Parse states array into EcuStateMachine.
    /// Uses JsonSerializer with HILJsonOptions for polymorphic EcuResponse deserialization.
    /// </summary>
    private static EcuStateMachine ParseStateMachine(JsonElement statesEl, List<IEcuResponseGenerator> generators)
    {
        var allTransitions = new List<EcuStateTransition>();

        foreach (var stateEl in statesEl.EnumerateArray())
        {
            var stateName = stateEl.GetProperty("name").GetString()!;
            var transitionsEl = stateEl.GetProperty("transitions");

            foreach (var transitionEl in transitionsEl.EnumerateArray())
            {
                var transition = ParseTransition(transitionEl, stateName);
                allTransitions.Add(transition);
            }
        }

        return new EcuStateMachine(allTransitions, generators);
    }

    /// <summary>
    /// Parse a single transition from JSON.
    /// Uses HILJsonOptions for polymorphic EcuResponse deserialization ($type discriminator).
    /// </summary>
    private static EcuStateTransition ParseTransition(JsonElement el, string stateName)
    {
        var serviceIdEl = el.GetProperty("serviceId");
        var serviceId = serviceIdEl.ValueKind == JsonValueKind.Number
            ? serviceIdEl.GetByte()
            : ParseHexString(serviceIdEl.GetString()!);

        byte? subFunction = null;
        if (el.TryGetProperty("subFunction", out var subFunc) && subFunc.ValueKind != JsonValueKind.Null)
        {
            subFunction = subFunc.ValueKind == JsonValueKind.Number
                ? subFunc.GetByte()
                : ParseHexString(subFunc.GetString()!);
        }

        byte[]? dataMask = null;
        byte[]? dataPattern = null;
        if (el.TryGetProperty("dataMask", out var mask))
        {
            dataMask = mask.EnumerateArray().Select(b => b.GetByte()).ToArray();
            dataPattern = el.GetProperty("dataPattern").EnumerateArray().Select(b => b.GetByte()).ToArray();
        }

        // Parse polymorphic response using HILJsonOptions
        var responseEl = el.GetProperty("response");
        var response = JsonSerializer.Deserialize<EcuResponse>(responseEl.GetRawText(), HILJsonOptions.Default)
            ?? throw new JsonException("Failed to parse response in transition.");

        string? toState = null;
        if (el.TryGetProperty("toState", out var toStateEl) && toStateEl.ValueKind != JsonValueKind.Null)
        {
            toState = toStateEl.GetString();
        }

        var delayMs = el.TryGetProperty("responseDelayMs", out var delay) ? delay.GetInt32() : 0;

        return new EcuStateTransition
        {
            FromState = stateName,
            ServiceId = serviceId,
            SubFunction = subFunction,
            DataMask = dataMask,
            DataPattern = dataPattern,
            Response = response,
            ToState = toState,
            ResponseDelayMs = delayMs
        };
    }

    /// <summary>
    /// Parse stateless rules into a state machine (wildcard transitions for backward compat).
    /// </summary>
    private static EcuStateMachine ParseRules(JsonElement rulesEl)
    {
        var rules = new List<UdsResponseRule>();
        foreach (var ruleEl in rulesEl.EnumerateArray())
        {
            rules.Add(ParseRule(ruleEl));
        }
        return EcuStateMachine.FromRules(rules);
    }

    private static uint ParseCanId(JsonElement element)
    {
        // CanIdJsonConverter writes "raw" as a number (e.g. 2016), but ECU script JSON
        // may also use string format (e.g. "0x7E0"). Handle both.
        if (element.ValueKind == JsonValueKind.Number)
            return element.GetUInt32();
        var s = element.GetString()!;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s[2..], NumberStyles.HexNumber)
            : uint.Parse(s);
    }

    private static byte ParseHexString(string s)
    {
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.Parse(s[2..], NumberStyles.HexNumber)
            : byte.Parse(s);
    }

    private static UdsResponseRule ParseRule(JsonElement el)
    {
        var serviceIdEl = el.GetProperty("serviceId");
        var serviceId = serviceIdEl.ValueKind == JsonValueKind.Number
            ? serviceIdEl.GetByte()
            : ParseHexString(serviceIdEl.GetString()!);
        var responseDataEl = el.GetProperty("responseData");
        var responseData = responseDataEl.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(responseDataEl.GetString()!)
            : responseDataEl.EnumerateArray().Select(b => b.GetByte()).ToArray();

        byte? subFunction = null;
        if (el.TryGetProperty("subFunction", out var subFunc) && subFunc.ValueKind != JsonValueKind.Null)
        {
            subFunction = subFunc.ValueKind == JsonValueKind.Number
                ? subFunc.GetByte()
                : ParseHexString(subFunc.GetString()!);
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

    private static List<IEcuResponseGenerator> GetBuiltInGenerators()
    {
        return new()
        {
            new Generators.SecurityAccessSeedGenerator(),
            new Generators.SecurityAccessVerifyKeyGenerator(),
            new Generators.ClearDtcGenerator(),
        };
    }
}
