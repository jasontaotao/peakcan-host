using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
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
}