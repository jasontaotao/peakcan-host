using System.Xml.Linq;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.Odx;

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
    /// <returns>List of transitions (may be empty if no extractable services found).</returns>
    /// <exception cref="OdxParseException">ODX has invalid namespace or is unreadable.</exception>
    public IReadOnlyList<EcuStateTransition> Load(string odxPath)
    {
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

        // Routine Control transitions (0x31) — wildcard (default state)
        var routines = RequestBasedMappers.ExtractRoutines(doc, ns);
        foreach (var routine in routines)
        {
            // Start (subFunc=0x01) — always generate
            transitions.Add(new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x31,
                SubFunction = 0x01,
                DataMask = new byte[] { 0xFF, 0xFF },
                DataPattern = new byte[] { (byte)((routine.Id >> 8) & 0xFF), (byte)(routine.Id & 0xFF) },
                Response = new StaticResponse(new byte[] { 0x71, 0x01 }),
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
                    Response = new StaticResponse(new byte[] { 0x71, 0x02 }),
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
                Response = new StaticResponse(new byte[] { 0x71, 0x03 }),
                ResponseDelayMs = 0
            });
        }

        return transitions;
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
