using System.Xml.Linq;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.Odx;

namespace PeakCan.Host.Infrastructure.HIL.Odx;

/// <summary>
/// Sprint 9: Adapts existing Core/Uds/Odx/ extractors to produce
/// EcuStateTransition lists for the stateful ECU simulation.
///
/// Reuses SecurityAccessExtractor + RequestBasedMappers (DidDop, EcuJob, ExtractRoutines)
/// rather than duplicating ODX parsing logic. Resolves XNamespace from the root element
/// and passes it through to all extractors.
/// </summary>
public sealed class OdxToEcuScriptAdapter
{
    /// <summary>
    /// Load an ODX file and extract stateful transitions.
    /// </summary>
    /// <param name="odxPath">Path to the .odx or .pdx file.</param>
    /// <param name="initialState">Out: the STATE-CHART start state (e.g. "Locked"),
    /// or "default" when the ODX has no SECURITY STATE-CHART (backward compatible).</param>
    /// <returns>List of transitions (may be empty if no extractable services found).</returns>
    /// <exception cref="OdxParseException">ODX has invalid namespace or is unreadable.</exception>
    public IReadOnlyList<EcuStateTransition> Load(string odxPath, out string initialState)
    {
        initialState = "default";

        var doc = XDocument.Load(odxPath);
        if (doc.Root is null)
            throw new OdxParseException("ODX document has no root element.");

        var ns = ResolveNamespace(doc.Root);

        var transitions = new List<EcuStateTransition>();

        // SecurityAccess transitions
        var secConfig = SecurityAccessExtractor.Extract(doc, ns);
        if (secConfig is { } cfg && cfg.SeedLength is { } seedLen && seedLen > 0)
        {
            // Seed request (0x27 0x01) → seedSent
            transitions.Add(new EcuStateTransition
            {
                FromState = null, // wildcard: matches any state (FSM starts in "default")
                ServiceId = 0x27,
                SubFunction = 0x01,
                Response = new DynamicResponse("SecurityAccessSeed"),
                ToState = "seedSent",
                ResponseDelayMs = 0
            });

            // Key verify (0x27 0x02) → unlocked
            transitions.Add(new EcuStateTransition
            {
                FromState = null, // wildcard: seed request can come from any state
                ServiceId = 0x27,
                SubFunction = 0x02,
                Response = new DynamicResponse("SecurityAccessVerifyKey"),
                ToState = "unlocked",
                ResponseDelayMs = 0
            });
        }

        // DID Read transitions (0x22) — wildcard (default state)
        var dids = RequestBasedMappers.ExtractDids(doc, ns);
        foreach (var (did, _) in dids)
        {
            transitions.Add(new EcuStateTransition
            {
                FromState = null, // wildcard: matches any state
                ServiceId = 0x22,
                DataMask = new byte[] { 0xFF, 0xFF },
                DataPattern = new byte[] { (byte)((did >> 8) & 0xFF), (byte)(did & 0xFF) },
                Response = new DynamicResponse("DidReadout"),
                ResponseDelayMs = 0
            });
        }

        // Routine Control transitions (0x31) — wildcard (default state).
        // Sprint 18 Inc 7: response bytes come from the ODX POS-RESPONSE chain
        // (ExtractRoutineResponses) instead of a hardcoded [0x71, subFunc].
        // Keyed by (routineId, subFunction) so Start/Stop/RequestResults each
        // echo their own subfunction byte (code-review H1 fix).
        var routineResponses = RequestBasedMappers.ExtractRoutineResponses(doc, ns);
        var routines = RequestBasedMappers.ExtractRoutines(doc, ns);
        foreach (var routine in routines)
        {
            // Fallback when the ODX has no extractable response payload.
            var startResp = routineResponses.TryGetValue((routine.Id, (byte)0x01), out var startBytes)
                ? startBytes
                : new byte[] { 0x71, 0x01 };
            var stopResp = routineResponses.TryGetValue((routine.Id, (byte)0x02), out var stopBytes)
                ? stopBytes
                : new byte[] { 0x71, 0x02 };
            var resultsResp = routineResponses.TryGetValue((routine.Id, (byte)0x03), out var resultsBytes)
                ? resultsBytes
                : new byte[] { 0x71, 0x03 };

            // Start (subFunc=0x01) — always generate
            transitions.Add(new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x31,
                SubFunction = 0x01,
                DataMask = new byte[] { 0xFF, 0xFF },
                DataPattern = new byte[] { (byte)((routine.Id >> 8) & 0xFF), (byte)(routine.Id & 0xFF) },
                Response = new StaticResponse(startResp),
                ResponseDelayMs = 0
            });

            // Stop (subFunc=0x02) — only if Stoppable
            if (routine.Stoppable)
            {
                transitions.Add(new EcuStateTransition
                {
                    FromState = null,
                    ServiceId = 0x31,
                    SubFunction = 0x02,
                    DataMask = new byte[] { 0xFF, 0xFF },
                    DataPattern = new byte[] { (byte)((routine.Id >> 8) & 0xFF), (byte)(routine.Id & 0xFF) },
                    Response = new StaticResponse(stopResp),
                    ResponseDelayMs = 0
                });
            }

            // RequestResults (subFunc=0x03) — always generate
            transitions.Add(new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x31,
                SubFunction = 0x03,
                DataMask = new byte[] { 0xFF, 0xFF },
                DataPattern = new byte[] { (byte)((routine.Id >> 8) & 0xFF), (byte)(routine.Id & 0xFF) },
                Response = new StaticResponse(resultsResp),
                ResponseDelayMs = 0
            });
        }

        // Sprint 18 Inc 7: apply STATE-CHART source/target states to transitions
        // whose (ServiceId, SubFunction) appears in the DIAG-SERVICE map.
        var stateChart = OdxStateChartExtractor.TryExtract(doc, ns, "SECURITY");
        if (stateChart is { } chart)
        {
            initialState = chart.StartState;

            var transitionMap = chart.Transitions.ToDictionary(t => t.TransitionId);
            var diagSvcTransitions = OdxStateChartExtractor.BuildDiagServiceTransitionMap(doc, ns);
            var diagSvcToRequest = BuildDiagServiceToRequestMap(doc, ns);

            var stateTransitionsByService = new Dictionary<ServiceRequest, List<(string From, string To)>>();
            foreach (var (svcId, transitionRefs) in diagSvcTransitions)
            {
                if (!diagSvcToRequest.TryGetValue(svcId, out var req)) continue;
                foreach (var transitionRef in transitionRefs)
                {
                    if (!transitionMap.TryGetValue(transitionRef, out var scTrans)) continue;
                    var key = new ServiceRequest(req.Sid, req.Sub);
                    if (!stateTransitionsByService.TryGetValue(key, out var list))
                        stateTransitionsByService[key] = list = new List<(string, string)>();
                    list.Add((scTrans.SourceState, scTrans.TargetState));
                }
            }

            // Code-review M1: replace each matched transition with ONE transition per
            // STATE-TRANSITION-REF (each distinct FromState), so e.g. Send_Key with
            // refs _639/_640/_641 yields Locked→UnlockedL1, UnlockedL1→UnlockedL1,
            // Unlocked_L2→UnlockedL1. Previously only st[0] survived, so key-verify
            // from any non-Locked state returned NRC 0x11.
            var expanded = new List<EcuStateTransition>();
            foreach (var t in transitions)
            {
                if (t.SubFunction is { } sub &&
                    stateTransitionsByService.TryGetValue(new ServiceRequest(t.ServiceId, sub), out var st))
                {
                    foreach (var (fromState, toState) in st)
                        expanded.Add(t with { FromState = fromState, ToState = toState });
                }
                else
                {
                    expanded.Add(t);
                }
            }
            transitions = expanded;

            // Code-review M1: the SecurityAccess seed request (0x27 0x01) has no
            // STATE-TRANSITION-REF in Demo_Cdd — it must NOT leave the machine in the
            // legacy "seedSent" state (which matches no chart state). Keep it in the
            // current state (ToState = null) so key-verify can still fire.
            for (int i = 0; i < transitions.Count; i++)
            {
                var t = transitions[i];
                if (t.ServiceId == 0x27 && t.SubFunction == 0x01 && t.FromState is null)
                {
                    transitions[i] = t with { ToState = null };
                }
            }
        }

        return transitions;
    }

    private readonly record struct ServiceRequest(byte Sid, byte Sub);

    /// <summary>
    /// Build DIAG-SERVICE XML id → (SID, subFunction). Reuses RequestBasedMappers'
    /// internal readers (widened to internal in Sprint 18 Inc 5).
    /// </summary>
    private static IReadOnlyDictionary<string, (byte Sid, byte Sub)> BuildDiagServiceToRequestMap(
        XDocument xdoc, XNamespace ns)
    {
        var requestById = new Dictionary<string, XElement>();
        foreach (var req in xdoc.Descendants(ns + "REQUEST"))
        {
            var id = (string?)req.Attribute("ID");
            if (id is not null) requestById[id] = req;
        }

        var result = new Dictionary<string, (byte Sid, byte Sub)>();
        foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
        {
            var svcId = (string?)svc.Attribute("ID");
            var reqRefEl = svc.Element(ns + "REQUEST-REF");
            if (svcId is null || reqRefEl is null) continue;
            var reqRefId = (string?)reqRefEl.Attribute("ID-REF");
            if (reqRefId is null || !requestById.TryGetValue(reqRefId, out var req)) continue;

            var sid = RequestBasedMappers.ReadServiceId(req, ns);
            var sub = RequestBasedMappers.ReadSubfunctionParam(req, ns);
            if (sid is not null)
                result[svcId] = (sid.Value, sub);
        }

        return result;
    }

    /// <summary>
    /// Resolve XNamespace from ODX root element.
    /// Accepts ODX 2.x namespace ("http://www.asam.net/xml/odx") or empty (OX-D).
    /// </summary>
    /// <exception cref="OdxParseException">Namespace is neither ODX 2.x nor empty.</exception>
    private static XNamespace ResolveNamespace(XElement root)
    {
        var ns = root.Name.Namespace;
        if (ns.NamespaceName == OdxParser.OdxNamespace || ns.NamespaceName == OdxParser.NoNamespace)
            return ns;

        throw new OdxParseException(
            $"Root element namespace '{ns.NamespaceName}' is not ODX namespace '{OdxParser.OdxNamespace}' or empty (no-namespace ODX-D).");
    }
}
