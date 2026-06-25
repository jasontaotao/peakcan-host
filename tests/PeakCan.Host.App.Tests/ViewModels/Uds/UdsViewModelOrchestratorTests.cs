using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
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
    public void Ctor_Wires_All_Four_PanelVMs_To_Shared_OutputLog()
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
}
