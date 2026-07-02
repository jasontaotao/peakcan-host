using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.Tests.ViewModels.Udx;

/// <summary>
/// End-to-end tests using a real OEM .odx-d file (Vector
/// CANdelaStudio export from a customer 38kWh BMS project;
/// supplied by user 2026-07-02). Skips when fixture absent so
/// CI does not fail without the OEM file.
///
/// Expected when v2.0.4 PATCH is GREEN:
///   - 4 routines (CheckProgrammingPreconditions_Start, CheckMemory_Start,
///     EraseMemory_Start, CheckProgrammingDependencies_Start)
///   - ~99 DTCs (P0xxx battery BMS codes)
///   - ≥7 DIDs (0x22 R + 0x2E RW services)
///   - 0 failures / HasError==false
/// </summary>
public class OdxImportServiceRealFileTests
{
    private static readonly string FixturePath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "Odx", "Demo_Cdd.odx-d"));

    private static OdxImportService NewService(
        out DidDatabase dids, out RoutineDatabase routines, out DtcDatabase dtcs)
    {
        dids = new DidDatabase(userJsonPath: null, logger: null);
        routines = new RoutineDatabase(userJsonPath: null, logger: null);
        dtcs = new DtcDatabase();
        return new OdxImportService(
            dids, routines, dtcs,
            new PdxReader(), new OdxParser(),
            NullLogger<OdxImportService>.Instance);
    }

    [Fact]
    public async Task RealFile_ImportAsync_SucceedsWithExpectedCounts()
    {
        if (!File.Exists(FixturePath)) return; // skip without fixture

        var svc = NewService(out var dids, out var routines, out var dtcs);

        var result = await svc.ImportAsync(FixturePath);

        result.HasError.Should().BeFalse(
            "real OEM .odx-d should import without errors; " +
            $"warnings: {string.Join("; ", result.Warnings)}");
        result.DtcCount.Should().BeGreaterThanOrEqualTo(50,
            "real file has ~99 DTCs; v2.0.0 PATCH returned 0");
        result.RoutineCount.Should().Be(4,
            "real file has 4 routines (CheckProgrammingPreconditions, " +
            "CheckMemory, EraseMemory, CheckProgrammingDependencies — all _Start)");
        result.DidCount.Should().BeGreaterThanOrEqualTo(1,
            "real file has 0x22/0x2E REQUESTs that should yield DIDs");
    }

    [Fact]
    public async Task RealFile_ImportAsync_RoutineIdsAreSensible()
    {
        if (!File.Exists(FixturePath)) return;

        var svc = NewService(out _, out var routines, out _);

        await svc.ImportAsync(FixturePath);

        var routineIds = routines.All.Select(r => r.Id).ToHashSet();
        routineIds.Should().NotBeEmpty();
        // Sample known routine ids from real file:
        // 514 (0x0202 = CheckMemory), 515 (0x0203 = checkProgrammingPreconditions).
        routineIds.Should().Contain(514);
        routineIds.Should().Contain(515);
    }

    [Fact]
    public async Task RealFile_ImportAsync_PopulatesDidLengthBytes()
    {
        if (!File.Exists(FixturePath)) return;

        var svc = NewService(out var dids, out _, out _);

        await svc.ImportAsync(FixturePath);

        // CellVolt_JG_Read corresponds to DID 0x0102 (258 dec). Its
        // POS-RESPONSE has 8 SEMANTIC="DATA" PARAMs: 2 referencing
        // DATA-OBJECT-PROP "Hex_182_Byte" (BIT-LENGTH=1456) + 6
        // referencing "HexDump_6_Byte" (BIT-LENGTH=48). Total =
        // 2*1456 + 6*48 = 3200 bits = 400 bytes. v2.0.4 set every
        // imported DID to LengthBytes=0; v2.0.5 should resolve the
        // POS-RESPONSE chain and populate LengthBytes correctly.
        var cellVolt = dids.Find(0x0102);
        cellVolt.Should().NotBeNull();
        cellVolt!.LengthBytes.Should().Be(400,
            "CellVolt_JG_Read POS-RESPONSE 8 DATA PARAMs sum to " +
            "2*1456 + 6*48 = 3200 bits = 400 bytes (full response body)");
    }
}
