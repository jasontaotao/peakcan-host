using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// Sprint 18 Inc 4: OdxStateChartExtractor — extracts STATE-CHART info from an
/// ODX document (chart name, start state, states, transitions) and maps each
/// DIAG-SERVICE to its STATE-TRANSITION-REF list.
///
/// Uses the real OEM Demo_Cdd.odx-d fixture (Vector CANdelaStudio export).
/// Soft-skips when the gitignored fixture is absent (same pattern as DemoCddSmokeTests).
/// </summary>
public class OdxStateChartExtractorTests
{
    private static readonly string DemoCddPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "Demo_Cdd.odx-d"));

    private static readonly string CompleteOdxPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "complete.odx"));

    private static XDocument LoadFixture(string path)
    {
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        return XDocument.Load(path);
    }

    private static XNamespace ResolveNamespace(XElement root)
    {
        var ns = root.Name.Namespace;
        return ns.NamespaceName == OdxParser.OdxNamespace || ns.NamespaceName == OdxParser.NoNamespace
            ? ns
            : throw new InvalidOperationException($"Unexpected ODX namespace: {ns.NamespaceName}");
    }

    [Fact]
    public void TryExtract_DemoCddSecurityChart_ReturnsLockedStartState()
    {
        var xdoc = LoadFixture(DemoCddPath);
        var ns = ResolveNamespace(xdoc.Root!);

        var chart = OdxStateChartExtractor.TryExtract(xdoc, ns, "SECURITY");

        Assert.NotNull(chart);
        Assert.Equal("Locked", chart.StartState);
        Assert.Equal("SecurityAccess", chart.ChartName);
    }

    [Fact]
    public void TryExtract_DemoCddDefaultChart_ReturnsFirstChart()
    {
        var xdoc = LoadFixture(DemoCddPath);
        var ns = ResolveNamespace(xdoc.Root!);

        var chart = OdxStateChartExtractor.TryExtract(xdoc, ns);

        Assert.NotNull(chart);
        // No semantic -> first STATE-CHART in document (Session, StartState="Default").
        Assert.Equal("Session", chart.ChartName);
        Assert.Equal("Default", chart.StartState);
    }

    [Fact]
    public void TryExtract_NoStateChart_ReturnsNull()
    {
        if (!File.Exists(CompleteOdxPath)) return; // skip without fixture
        var xdoc = LoadFixture(CompleteOdxPath);
        var ns = ResolveNamespace(xdoc.Root!);

        var chart = OdxStateChartExtractor.TryExtract(xdoc, ns, "SECURITY");
        var chartDefault = OdxStateChartExtractor.TryExtract(xdoc, ns);

        Assert.Null(chart);
        Assert.Null(chartDefault);
    }

    [Fact]
    public void TryExtract_DemoCddSecurityChart_Has9Transitions()
    {
        var xdoc = LoadFixture(DemoCddPath);
        var ns = ResolveNamespace(xdoc.Root!);

        var chart = OdxStateChartExtractor.TryExtract(xdoc, ns, "SECURITY");

        Assert.NotNull(chart);
        Assert.Equal(9, chart.Transitions.Count);
        // Spot-check a transition whose source/target are explicit states.
        Assert.Contains(chart.Transitions, t => t.TransitionId == "_639"
            && t.SourceState == "Locked" && t.TargetState == "UnlockedL1");
    }

    [Fact]
    public void BuildDiagServiceTransitionMap_DemoCdd_ReturnsTransitionRefs()
    {
        var xdoc = LoadFixture(DemoCddPath);
        var ns = ResolveNamespace(xdoc.Root!);

        var map = OdxStateChartExtractor.BuildDiagServiceTransitionMap(xdoc, ns);

        Assert.NotNull(map);
        Assert.True(map.TryGetValue("_637", out var refs), "DIAG-SERVICE _637 not in map");
        Assert.Contains("_639", refs);
        Assert.Contains("_640", refs);
        Assert.Contains("_641", refs);
    }
}
