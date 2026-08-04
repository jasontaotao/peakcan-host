using System.Text.Json;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Serialization;

namespace PeakCan.Host.Infrastructure.HIL.Odx;

/// <summary>
/// Imports ODX (Open Diagnostic Data Exchange) files and converts them to
/// peakcan-hil ECU script JSON format. Uses OdxToEcuScriptAdapter to extract
/// stateful transitions and groups them into states.
/// </summary>
public static class OdxEcuScriptImporter
{
    /// <summary>
    /// Import an ODX file and convert to ECU script JSON (states format).
    /// </summary>
    public static string ImportToJson(
        string odxPath, string ecuName, uint requestId, uint responseId)
    {
        var adapter = new OdxToEcuScriptAdapter();
        var transitions = adapter.Load(odxPath, out var initialState);

        if (transitions.Count == 0)
            throw new InvalidOperationException($"No UDS services found in ODX file: {odxPath}");

        // Group transitions by FromState to create states.
        // Transitions with FromState=null (wildcard) go into a "wildcard" state
        // so the FSM can match them from any current state.
        var stateGroups = transitions
            .GroupBy(t => t.FromState ?? "wildcard")
            .OrderBy(g => g.Key == "wildcard" ? 0 : 1) // wildcard first
            .ToList();

        var states = stateGroups.Select(g => new
        {
            name = g.Key,
            transitions = g.Select(t => new
            {
                serviceId = $"0x{t.ServiceId:X2}",
                subFunction = t.SubFunction,
                dataMask = t.DataMask,
                dataPattern = t.DataPattern,
                response = t.Response,  // EcuResponse with [JsonPolymorphic] serializes as $type discriminator
                toState = t.ToState,
                responseDelayMs = t.ResponseDelayMs
            }).ToList()
        });

        var script = new
        {
            name = ecuName,
            initialState,  // Sprint 18 Inc 7: STATE-CHART start state (e.g. "Locked")
            canIds = new { requestId = $"0x{requestId:X3}", responseId = $"0x{responseId:X3}" },
            states
        };

        return JsonSerializer.Serialize(script, HILJsonOptions.Default);
    }
}
