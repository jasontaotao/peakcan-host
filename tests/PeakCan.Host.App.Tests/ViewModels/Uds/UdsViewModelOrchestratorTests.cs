using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for the new thin <see cref="UdsViewModel"/> orchestrator at
/// <c>PeakCan.Host.App.ViewModels.Uds</c>. Covers panel wiring + identity,
/// shared log forwarding via <see cref="SessionPanelViewModel.SetDefaultSessionCommand"/>
/// (which internally calls the panel's private AppendLog), ClearOutput
/// emptying the shared collection, and null-arg ArgumentNullException.
///
/// The old 279-line monolith at <c>PeakCan.Host.App.ViewModels</c> namespace
/// coexists with this orchestrator until Task 7 deletes it.
/// </summary>
public sealed class UdsViewModelOrchestratorTests
{
    private sealed class FakeUdsClient : UdsClient
    {
        public FakeUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        // Override so SetDefaultSessionCommand doesn't hit the real (time-out) IsoTp path.
        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
            => Task.FromResult(new DiagnosticSessionResponse
            {
                SessionType = sessionType,
                P2          = 50,
                P2Star      = 5000,
            });
    }

    [Fact]
    public void Ctor_Stores_All_Four_PanelVm_References()
    {
        var uds = new FakeUdsClient();
        var session = new SessionPanelViewModel(uds, NullLogger<SessionPanelViewModel>.Instance);
        var did     = new DidPanelViewModel(uds, new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance));
        var routine = new RoutinePanelViewModel(uds, new RoutineDatabase(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance));
        var dtc     = new DtcPanelViewModel(uds);

        var vm = new UdsViewModel(session, did, routine, dtc);

        vm.Session.Should().BeSameAs(session);
        vm.Did.Should().BeSameAs(did);
        vm.Routine.Should().BeSameAs(routine);
        vm.Dtc.Should().BeSameAs(dtc);
        // All four panel VMs should have been attached to the same OutputLog.
        vm.OutputLog.Should().BeEmpty();
    }

    [Fact]
    public async Task Ctor_Appending_Log_From_Session_Via_Command_Appears_In_OutputLog()
    {
        var uds = new FakeUdsClient();
        var session = new SessionPanelViewModel(uds, NullLogger<SessionPanelViewModel>.Instance);
        var did     = new DidPanelViewModel(uds, new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance));
        var routine = new RoutinePanelViewModel(uds, new RoutineDatabase(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance));
        var dtc     = new DtcPanelViewModel(uds);

        var vm = new UdsViewModel(session, did, routine, dtc);

        await session.SetDefaultSessionCommand.ExecuteAsync(null);

        vm.OutputLog.Should().Contain(l => l.Level == "Info" && l.Message == "Session → Default");
    }

    [Fact]
    public void ClearOutputCommand_Clears_OutputLog()
    {
        var uds = new FakeUdsClient();
        var vm = new UdsViewModel(
            new SessionPanelViewModel(uds, NullLogger<SessionPanelViewModel>.Instance),
            new DidPanelViewModel(uds, new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance)),
            new RoutinePanelViewModel(uds, new RoutineDatabase(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance)),
            new DtcPanelViewModel(uds));
        vm.OutputLog.Add(new UdsLogLine("t", "Info", "seed"));

        vm.ClearOutputCommand.Execute(null);

        vm.OutputLog.Should().BeEmpty();
    }

    [Fact]
    public void Ctor_With_Null_Session_Throws()
    {
        var uds = new FakeUdsClient();
        var did     = new DidPanelViewModel(uds, new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance));
        var routine = new RoutinePanelViewModel(uds, new RoutineDatabase(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance));
        var dtc     = new DtcPanelViewModel(uds);

        var act = () => new UdsViewModel(null!, did, routine, dtc);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---- C4 Flashing panel (6-arg production ctor) ----

    private static (SessionPanelViewModel, DidPanelViewModel, RoutinePanelViewModel, DtcPanelViewModel, FlashPanelViewModel)
        BuildProductionPanels()
    {
        // 6-arg production ctor wires all six panels; the Flash panel here is a REAL
        // FlashPanelViewModel backed by the production SecondaryFlashStackFactory (real
        // ChannelRouter/UdsTimer/loggers, dummy CoreSendService) so the ctor surface is
        // exercised exactly as DI constructs it. No Start is triggered — wiring only.
        var uds = new FakeUdsClient();
        var session = new SessionPanelViewModel(uds, NullLogger<SessionPanelViewModel>.Instance);
        var did     = new DidPanelViewModel(uds, new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance));
        var routine = new RoutinePanelViewModel(uds, new RoutineDatabase(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance));
        var dtc     = new DtcPanelViewModel(uds);
        var factory = new SecondaryFlashStackFactory(
            new CoreSendService(NullLogger<SendService>.Instance),
            new ChannelRouter(),
            new UdsTimer(),
            NullLogger<IsoTpLayer>.Instance,
            NullLogger<UdsSession>.Instance,
            NullLogger<SecondaryFlashStack>.Instance);
        var flash = new FlashPanelViewModel(factory, NullLogger<FlashPanelViewModel>.Instance);
        return (session, did, routine, dtc, flash);
    }

    [Fact]
    public void Production_6Arg_Ctor_Binds_Flash_Panel()
    {
        var (session, did, routine, dtc, flash) = BuildProductionPanels();
        var odx = new OdxImportViewModel(new StubOdx());

        var vm = new UdsViewModel(session, did, routine, dtc, odx, flash);

        vm.Flash.Should().BeSameAs(flash, "the 6-arg ctor binds the Flash panel verbatim for the Flashing tab");
        vm.Session.Should().BeSameAs(session);
        vm.Did.Should().BeSameAs(did);
        vm.Routine.Should().BeSameAs(routine);
        vm.Dtc.Should().BeSameAs(dtc);
        // Flash panel's default profile is live + non-null — the Flashing tab DataGrid binds it.
        vm.Flash.CurrentProfile.Should().NotBeNull();
        vm.Flash.CurrentProfile.Steps.Should().NotBeEmpty();
    }

    [Fact]
    public void Production_6Arg_Ctor_Null_Flash_Throws()
    {
        var (session, did, routine, dtc, _) = BuildProductionPanels();
        var odx = new OdxImportViewModel(new StubOdx());

        var act = () => new UdsViewModel(session, did, routine, dtc, odx, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Minimal stub IOdxImportService so the 6-arg ctor test can build OdxImportViewModel.</summary>
    internal sealed class StubOdx : IOdxImportService
    {
        public Task<PeakCan.Host.Core.Uds.Odx.OdxImportResult> ImportAsync(string odxPath, System.Threading.CancellationToken ct = default)
            => Task.FromResult(PeakCan.Host.Core.Uds.Odx.OdxImportResult.Ok(0, 0, 0, System.Array.Empty<string>()));
    }
}
