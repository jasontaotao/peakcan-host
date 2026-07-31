using System.Globalization;
using System.Text.Json;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Loads an ECU simulator script from JSON. Parses CAN IDs and states/rules.
/// Swaps RequestId/ResponseId to produce ECU-perspective CanIdConfig.
/// Supports both stateless ("rules") and stateful ("states") JSON formats.
/// Sprint 10: Added external generator support and didValues injection.
/// </summary>
public static class EcuScriptLoader
{
    public static EcuScript Load(string path, IEnumerable<IEcuResponseGenerator>? externalGenerators = null)
    {
        var json = File.ReadAllText(path);
        return Parse(json, externalGenerators);
    }

    public static EcuScript Parse(string json, IEnumerable<IEcuResponseGenerator>? externalGenerators = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseEcuScript(doc.RootElement, externalGenerators);
    }

    public static EcuScript ParseEcuScript(JsonElement element, IEnumerable<IEcuResponseGenerator>? externalGenerators = null)
    {
        var canIds = ParseCanIds(element.GetProperty("canIds"));

        bool hasStates = element.TryGetProperty("states", out var statesEl);
        bool hasRules = element.TryGetProperty("rules", out var rulesEl);

        if (hasStates && hasRules)
            throw new JsonException("Cannot specify both 'states' and 'rules' in ECU script.");

        var mergedGenerators = GeneratorPluginLoader.MergeGenerators(GetBuiltInGenerators(), externalGenerators).ToList();

        EcuStateMachine stateMachine;
        if (hasStates)
        {
            stateMachine = ParseStateMachine(statesEl, mergedGenerators);
        }
        else if (hasRules)
        {
            stateMachine = ParseRules(rulesEl);
        }
        else
        {
            throw new JsonException("ECU script must contain either 'states' or 'rules'.");
        }

        // Parse didValues (optional)
        Dictionary<ushort, byte[]>? didValues = null;
        if (element.TryGetProperty("didValues", out var didValuesEl))
        {
            didValues = new Dictionary<ushort, byte[]>();
            foreach (var prop in didValuesEl.EnumerateObject())
            {
                var didStr = prop.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? prop.Name[2..] : prop.Name;
                var did = ushort.Parse(didStr, NumberStyles.HexNumber);
                var value = prop.Value.EnumerateArray().Select(b => b.GetByte()).ToArray();
                didValues[did] = value;
            }
        }

        // Inject didValues into context
        if (didValues is { Count: > 0 })
        {
            stateMachine.Context.Set("DidValues", didValues);
        }

        return new EcuScript(
            Name: element.GetProperty("name").GetString()!,
            CanIds: canIds,
            StateMachine: stateMachine,
            DidValues: didValues);
    }

    private static CanIdConfig ParseCanIds(JsonElement canIdsEl)
    {
        var requestIdHil = ParseCanId(canIdsEl.GetProperty("requestId"));
        var responseIdHil = ParseCanId(canIdsEl.GetProperty("responseId"));
        var isExtended = canIdsEl.TryGetProperty("isExtendedFrame", out var ext) && ext.GetBoolean();

        return new CanIdConfig
        {
            RequestId = responseIdHil,
            ResponseId = requestIdHil,
            IsExtendedFrame = isExtended
        };
    }

    private static EcuStateMachine ParseStateMachine(JsonElement statesEl, List<IEcuResponseGenerator> generators)
    {
        var allTransitions = new List<EcuStateTransition>();

        foreach (var stateEl in statesEl.EnumerateArray())
        {
            var stateName = stateEl.GetProperty("name").GetString()!;
            // "wildcard" is a special state name meaning FromState=null (matches any state)
            var fromState = stateName == "wildcard" ? null : stateName;
            var transitionsEl = stateEl.GetProperty("transitions");

            foreach (var transitionEl in transitionsEl.EnumerateArray())
            {
                var transition = ParseTransition(transitionEl, fromState);
                allTransitions.Add(transition);
            }
        }

        return new EcuStateMachine(allTransitions, generators);
    }

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
            new Generators.DidReadoutGenerator(),
            new Generators.DidWriteGenerator(),
        };
    }
}
