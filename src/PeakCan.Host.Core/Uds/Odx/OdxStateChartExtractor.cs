using System.Xml.Linq;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Sprint 18 Inc 4: extracts STATE-CHART definitions from an ODX document and
/// maps each DIAG-SERVICE to its STATE-TRANSITION-REF list.
///
/// Chart selection by semantic: <see cref="TryExtract(XDocument, XNamespace, string?)"/>
/// returns the first STATE-CHART whose SEMANTIC element matches <paramref name="semantic"/>,
/// or the first chart in document order when <paramref name="semantic"/> is null.
/// Returns null when no STATE-CHART element exists (backward-compatible documents).
/// </summary>
public static class OdxStateChartExtractor
{
    /// <summary>
    /// Extract a STATE-CHART by semantic (or the first chart when null).
    /// Returns null if the document contains no STATE-CHART.
    /// </summary>
    public static OdxStateChartInfo? TryExtract(XDocument xdoc, XNamespace ns, string? semantic = null)
    {
        ArgumentNullException.ThrowIfNull(xdoc);

        var charts = xdoc.Descendants(ns + "STATE-CHART").ToList();
        if (charts.Count == 0)
            return null;

        XElement? chart = semantic is null
            ? charts[0]
            : charts.FirstOrDefault(c => (string?)c.Element(ns + "SEMANTIC") == semantic);

        if (chart is null)
            return null;

        var chartName = (string?)chart.Element(ns + "SHORT-NAME") ?? "";
        var startState = (string?)chart.Element(ns + "START-STATE-SNREF")?.Attribute("SHORT-NAME")
                         ?? "";

        // STATES -> STATE -> SHORT-NAME
        var states = new List<string>();
        var statesEl = chart.Element(ns + "STATES");
        if (statesEl is not null)
        {
            foreach (var state in statesEl.Elements(ns + "STATE"))
            {
                var name = (string?)state.Element(ns + "SHORT-NAME");
                if (name is not null)
                    states.Add(name);
            }
        }

        // STATE-TRANSITIONS -> STATE-TRANSITION (SOURCE-SNREF / TARGET-SNREF)
        var transitions = new List<StateChartTransition>();
        var transitionsEl = chart.Element(ns + "STATE-TRANSITIONS");
        if (transitionsEl is not null)
        {
            foreach (var t in transitionsEl.Elements(ns + "STATE-TRANSITION"))
            {
                transitions.Add(new StateChartTransition(
                    TransitionId: (string?)t.Attribute("ID") ?? "",
                    SourceState: (string?)t.Element(ns + "SOURCE-SNREF")?.Attribute("SHORT-NAME") ?? "",
                    TargetState: (string?)t.Element(ns + "TARGET-SNREF")?.Attribute("SHORT-NAME") ?? ""));
            }
        }

        return new OdxStateChartInfo(chartName, startState, states, transitions);
    }

    /// <summary>
    /// Map every DIAG-SERVICE id to its STATE-TRANSITION-REFS ID-REF list
    /// (ODX path: DIAG-SERVICE → STATE-TRANSITION-REFS → STATE-TRANSITION-REF → ID-REF).
    /// Services without a STATE-TRANSITION-REFS block map to an empty list.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDiagServiceTransitionMap(
        XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);

        var map = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
        {
            var svcId = (string?)svc.Attribute("ID");
            if (svcId is null)
                continue;

            var refs = new List<string>();
            foreach (var refEl in svc.Elements(ns + "STATE-TRANSITION-REFS")
                                      .Elements(ns + "STATE-TRANSITION-REF"))
            {
                var refId = (string?)refEl.Attribute("ID-REF");
                if (refId is not null)
                    refs.Add(refId);
            }

            map[svcId] = refs;
        }

        return map;
    }
}
