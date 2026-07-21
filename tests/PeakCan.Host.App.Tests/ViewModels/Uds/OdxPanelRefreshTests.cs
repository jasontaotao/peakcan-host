using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// v2.0.6 PATCH Bug-1 regression: after an ODX import the Did / Routine
/// DataGrids must reflect the imported definitions. Pre-v2.0.6 the
/// panel VMs populated their ObservableCollection only in the
/// constructor and never refreshed — calling
/// <see cref="DidDatabase.AddRange"/> / <see cref="RoutineDatabase.AddRange"/>
/// from <c>OdxImportService</c> mutated the database in-place but the
/// UI stayed frozen on the ctor-time snapshot.
/// </summary>
public sealed class OdxPanelRefreshTests
{
    private sealed class NoopUdsClient : UdsClient
    {
        public NoopUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }
    }

    private static DidDatabase NewDidDb() =>
        new(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);

    private static RoutineDatabase NewRoutineDb() =>
        new(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance);

    [Fact]
    public void DidPanelViewModel_RefreshFromDatabase_AddsNewRowsFromDatabase()
    {
        var db = NewDidDb();
        var vm = new DidPanelViewModel(new NoopUdsClient(), db);

        // Pre-condition: ctor populated with built-in defaults (5 entries).
        vm.Dids.Should().HaveCount(5, "DidDatabase built-in defaults have 5 entries");

        // Simulate ODX import: add a new DID via AddRange (matches what
        // OdxImportService does).
        db.AddRange(new[]
        {
            new DidDefinition(Id: 0x0102, Name: "CellVolt_JG_Read",
                Description: "DID 0x0102 (R, 400B)", LengthBytes: 400, Writable: false),
        }, out _);

        // Pre-v2.0.6: Dids would still have 5 entries (UI frozen on ctor snapshot).
        // Post-v2.0.6: RefreshFromDatabase picks up the new entry.
        vm.RefreshFromDatabase();

        vm.Dids.Should().HaveCount(6, "RefreshFromDatabase must re-read DidDatabase.All");
        vm.Dids.Should().ContainSingle(d => d.Id == 0x0102 && d.LengthBytes == 400);
    }

    [Fact]
    public void DidPanelViewModel_RefreshFromDatabase_OverridesExistingRows_LastWins()
    {
        // Mirror DidDatabase.AddRange's documented last-wins semantics:
        // importing an ODX file that contains the built-in 0xF190 (VIN)
        // entry should update the existing row, not add a duplicate.
        var db = NewDidDb();
        var vm = new DidPanelViewModel(new NoopUdsClient(), db);

        // Pre-condition: 0xF190 (VIN) with LengthBytes=17 (built-in).
        vm.Dids.Should().ContainSingle(d => d.Id == 0xF190).Which.LengthBytes.Should().Be(17);

        db.AddRange(new[]
        {
            new DidDefinition(Id: 0xF190, Name: "VIN_override",
                Description: "ODX-overridden", LengthBytes: 32, Writable: true),
        }, out _);

        vm.RefreshFromDatabase();

        vm.Dids.Where(d => d.Id == 0xF190).Should().HaveCount(1,
            "duplicate AddRange should replace, not append (last-wins on Did.Id)");
        vm.Dids.Single(d => d.Id == 0xF190).Name.Should().Be("VIN_override");
        vm.Dids.Single(d => d.Id == 0xF190).LengthBytes.Should().Be(32);
    }

    [Fact]
    public void RoutinePanelViewModel_RefreshFromDatabase_AddsNewRowsFromDatabase()
    {
        var db = NewRoutineDb();
        var vm = new RoutinePanelViewModel(new NoopUdsClient(), db);

        vm.Routines.Should().BeEmpty("RoutineDatabase built-in defaults are empty");

        // Simulate ODX import of the real 4 routines from Demo_Cdd.odx-d
        // (verified in v2.0.4 PATCH: routine ids 514, 515, 518, 519).
        db.AddRange(new[]
        {
            new RoutineDefinition(Id: 514, Name: "EraseMemory_Start",
                Description: "EraseMemory (Start)", Startable: true, Stoppable: false),
            new RoutineDefinition(Id: 515, Name: "CheckProgrammingPreconditions_Start",
                Description: "CheckProgrammingPreconditions (Start)", Startable: true, Stoppable: false),
        }, out _);

        vm.RefreshFromDatabase();

        vm.Routines.Should().HaveCount(2);
        vm.Routines.Should().Contain(r => r.Id == 514 && r.Name == "EraseMemory_Start");
        vm.Routines.Should().Contain(r => r.Id == 515);
    }

    // v3.49.0 MINOR T4.2 — DidPanelViewModel 应把 DidDefinition.Fields
    // 透传到 DidRow,使 UI 的 TypeDisplay 列与解码详情可用。
    public class DidPanelFieldPropagationTests
    {
        [Fact]
        public void Ctor_MapsDidDefinitionFieldsToRow()
        {
            var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
            // 模拟一个带类型表的 DID 0xF401: 温度,16-bit UINT32 + LINEAR + °C
            db.AddRange(new[]
            {
                new DidDefinition(Id: 0xF401, Name: "Temp",
                    Description: "Temp", LengthBytes: 2, Writable: false)
                with { Fields = new[]
                {
                    new DidField("Temp", 16, 0, DidBaseType.UInt32,
                        CompuMethod.LinearOf(0.5, -40.0), new DidUnit("_C", "°C")),
                } },
            }, out _);

            var vm = new DidPanelViewModel(new NoopUdsClient(), db);

            var row = vm.Dids.Should().ContainSingle(d => d.Id == 0xF401).Which;
            row.Fields.Should().HaveCount(1);
            row.TypeDisplay.Should().Be("UInt32[16]");
        }

        [Fact]
        public void RefreshFromDatabase_InjectsFieldsAfterOdxImport()
        {
            var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
            var vm = new DidPanelViewModel(new NoopUdsClient(), db);

            // 构造期 VIN 无字段表 → TypeDisplay = "(no type)"
            vm.Dids.Single(d => d.Id == 0xF190).TypeDisplay.Should().Be("(no type)");

            // 模拟 ODX import 重写 VIN 为带 ASCII[17B] 字段表
            db.AddRange(new[]
            {
                new DidDefinition(Id: 0xF190, Name: "VIN",
                    Description: "VIN", LengthBytes: 17, Writable: false)
                with { Fields = new[]
                {
                    new DidField("VIN", 17 * 8, 0, DidBaseType.AsciiString, null, null),
                } },
            }, out _);

            vm.RefreshFromDatabase();

            var vin = vm.Dids.Single(d => d.Id == 0xF190);
            vin.Fields.Should().HaveCount(1);
            vin.TypeDisplay.Should().Be("AsciiString[17B]");
        }
    }
}
