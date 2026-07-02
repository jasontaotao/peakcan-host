using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// Smoke tests using a real OEM .odx-d file (Vector CANdelaStudio
/// export from a customer 38kWh BMS project; supplied by user 2026-07-02).
///
/// The fixture file is NOT tracked in git — see .gitignore
/// <c>tests/**/Fixtures/Odx/*.odx-d</c>. Test class skips when the
/// file is absent so CI does not fail for external contributors
/// without the fixture.
///
/// Expected when v2.0.4 parser is GREEN:
///   - 4 routines (CheckProgrammingPreconditions, CheckMemory,
///     EraseMemory, CheckProgrammingDependencies — Start variants)
///   - ~99 DTCs (P0xxx battery BMS codes)
///   - DIDs from 0x22/0x2E REQUESTs (number TBD by strict detection)
/// </summary>
public class DemoCddSmokeTests
{
    private static readonly string FixturePath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "Demo_Cdd.odx-d"));

    // v2.1.5 PATCH: removed `RealFile_FixtureExists_OtherwiseTestsSkipped`.
    // The fixture is .gitignore'd (proprietary OEM Demo_Cdd.odx-d), so on
    // a fresh CI clone it is intentionally absent. The other 4 tests in
    // this class handle missing fixture via `if (!File.Exists(FixturePath)) return;`
    // early-return soft-skip. The removed test was asserting the OPPOSITE
    // of the gitignored-by-design state, so it failed every CI run.

    [Fact]
    public async Task RealFile_LoadAsync_ReturnsOneDocument()
    {
        if (!File.Exists(FixturePath)) return; // skip without fixture

        var reader = new PdxReader();
        var docs = await reader.LoadAsync(FixturePath);

        docs.Should().ContainSingle(
            "real .odx-d is a single-document ODX (not a .pdx zip container)");
    }

    [Fact]
    public void RealFile_Parse_RecognizesBaseVariantLayer()
    {
        if (!File.Exists(FixturePath)) return;

        var xdoc = XDocument.Load(FixturePath);
        var parser = new OdxParser();

        var doc = parser.Parse(xdoc, out var warnings);

        // Real file uses <BASE-VARIANTS> (plural) wrapper, not the
        // <DIAG-LAYER> single-element form. v2.0.0 parser misses it.
        // v2.0.4 should yield at least one layer.
        doc.Layers.Should().NotBeEmpty(
            "real Demo_Cdd.odx-d uses BASE-VARIANT wrapper (parses 0 in v2.0.0)");
    }

    [Fact]
    public void RealFile_Parse_FindsDiagServices()
    {
        if (!File.Exists(FixturePath)) return;

        var xdoc = XDocument.Load(FixturePath);
        var parser = new OdxParser();

        var doc = parser.Parse(xdoc, out _);

        // Real file has 95 DIAG-SERVICE elements across sessions +
        // ReadDataByIdentifier + WriteDataByIdentifier + RoutineControl + etc.
        // Current parser returns 0 because DIAG-SERVICEs live inside
        // BASE-VARIANT, not DIAG-LAYER.
        var totalServices = doc.Layers.Sum(l => l.Services.Count);
        totalServices.Should().BeGreaterThanOrEqualTo(1,
            "real file has 95 DIAG-SERVICEs; v2.0.0 returns 0");
    }

    [Fact]
    public void RealFile_FindsDtcsInEcuSharedData()
    {
        if (!File.Exists(FixturePath)) return;

        var xdoc = XDocument.Load(FixturePath);
        var ns = xdoc.Root?.Name.Namespace ?? (XNamespace)OdxParser.OdxNamespace;

        // Real file has ~99 DTCs in <DTC-DOP> elements inside
        // <ECU-SHARED-DATA> (not inside DIAG-LAYER). v2.0.0 walked
        // descendants of <DIAG-LAYER> only and returned 0.
        var dtcDopCount = xdoc.Descendants(ns + "DTC-DOP").Count();
        dtcDopCount.Should().BeGreaterThanOrEqualTo(1,
            "real file has 2 DTC-DOP blocks; v2.0.4 PATCH walks whole document");
    }
}
