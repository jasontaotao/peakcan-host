using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.FlashPipeline;
using PeakCan.HIL.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for <see cref="TransportParamsViewModel"/> — the read-only communication-parameters
/// panel. Follows the SessionPanelViewModelTests pattern: a hand-rolled
/// <c>UdsClient</c> subclass over a delegate-sink <see cref="IsoTpLayer"/>, assertions via
/// FluentAssertions on the observable string properties after an explicit <see cref="TransportParamsViewModel.Poll"/>
/// (DispatcherTimer does not fire in STA xunit runs).
/// </summary>
public sealed class TransportParamsViewModelTests
{
    /// <summary>UdsClient double whose session-control override behaves like the base class
    /// (updates UdsSession) without touching the wire.</summary>
    private sealed class ParamsUdsClient : UdsClient
    {
        public ParamsUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
        {
            // Mirror the real base behaviour (parse + Session.SetSession) without wire traffic.
            Session.SetSession(sessionType, p2: 25, p2Star: 5000);
            return Task.FromResult(new DiagnosticSessionResponse
            {
                SessionType = sessionType,
                P2 = 25,
                P2Star = 5000
            });
        }
    }

    /// <summary>Fake secondary stack for the params-panel active-stack branch.</summary>
    private sealed class FakeFlashStack : ISecondaryFlashStack
    {
        public UdsClient Client { get; }
        public IsoTpLayer Transport { get; } = new(new CanIdConfig { RequestId = 0x7E2, ResponseId = 0x7EA }, _ => { });
        public FakeFlashStack(UdsClient client) => Client = client;
        public void AttachToRouter() { }
        public void DetachFromRouter() { }
        public void Dispose() { }
    }

    /// <summary>Refusing factory — the params VM never builds stacks; Start is not exercised here.</summary>
    private sealed class RefusingFactory : ISecondaryFlashStackFactory
    {
        public ISecondaryFlashStack Build(FlashStepSnapshot securityStep, FlashProfile profile)
            => throw new NotImplementedException("params-panel tests never start a flash run.");
    }

    private static TransportParamsViewModel NewVm(
        ParamsUdsClient? client = null,
        FlashPanelViewModel? flash = null)
        => new(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            client ?? new ParamsUdsClient(),
            flash ?? new FlashPanelViewModel(new RefusingFactory(), NullLogger<FlashPanelViewModel>.Instance),
            NullLogger<TransportParamsViewModel>.Instance);

    // ---- Diagnostic column -------------------------------------------------

    [Fact]
    public void Poll_DiagnosticColumn_Shows_LocalDefaults_Before_Session_Control()
    {
        var vm = NewVm();
        vm.Poll();

        vm.DiagTransport.Should().Be("0x7E0 → 0x7E8");
        vm.DiagSession.Should().Be("Default");
        vm.DiagP2.Should().Be("50 ms (本地默认)");
        vm.DiagP2Star.Should().Be("5000 ms (本地默认)");
        vm.DiagStMin.Should().Be("— (未收到 FC)");
        vm.DiagBlockSize.Should().Be("— (未收到 FC)");
        vm.DiagNBs.Should().Be("1000 ms");
        vm.DiagNCr.Should().Be("1000 ms");
        vm.DiagS3Failures.Should().Be("0");
    }

    [Fact]
    public async Task Poll_DiagnosticColumn_Shows_EcuNegotiated_After_Session_Control()
    {
        var client = new ParamsUdsClient();
        var vm = NewVm(client);

        await client.DiagnosticSessionControlAsync(0x03);
        vm.Poll();

        vm.DiagSession.Should().Be("Programming");
        vm.DiagP2.Should().Be("25 ms (ECU 0x10 协商)");
        vm.DiagP2Star.Should().Be("5000 ms (ECU 0x10 协商)");
    }

    // ---- Flash column ------------------------------------------------------

    [Fact]
    public void Poll_FlashColumn_Shows_NotRunning_When_Idle()
    {
        var vm = NewVm();
        vm.Poll();

        vm.FlashRunState.Should().Be("未运行");
        vm.FlashTransport.Should().Be("未运行");
        vm.FlashP2.Should().Be("未运行");
        vm.FlashStMin.Should().Be("未运行");
    }

    [Fact]
    public void Poll_FlashColumn_Shows_Active_Stack_Values_When_Set()
    {
        var flash = new FlashPanelViewModel(new RefusingFactory(), NullLogger<FlashPanelViewModel>.Instance);
        var stackClient = new ParamsUdsClient();
        stackClient.Session.SetSession(0x03, p2: 25, p2Star: 5000);
        var vm = NewVm(flash: flash);

        flash.SetActiveStackForTesting(new FakeFlashStack(stackClient));
        vm.Poll();

        vm.FlashRunState.Should().Be("运行中");
        vm.FlashTransport.Should().Be("0x7E2 → 0x7EA");
        vm.FlashSession.Should().Be("Programming");
        vm.FlashP2.Should().Be("25 ms (ECU 0x10 协商)");
        vm.FlashNBs.Should().Be("1000 ms");
        vm.FlashStMin.Should().Be("— (未收到 FC)", "the fake transport never received a Flow Control");
    }

    [Fact]
    public void Poll_FlashColumn_Returns_To_NotRunning_After_Stack_Cleared()
    {
        var flash = new FlashPanelViewModel(new RefusingFactory(), NullLogger<FlashPanelViewModel>.Instance);
        var vm = NewVm(flash: flash);

        flash.SetActiveStackForTesting(new FakeFlashStack(new ParamsUdsClient()));
        vm.Poll();
        vm.FlashRunState.Should().Be("运行中");

        // Mirrors RunFlashOnceAsync's finally: cleared BEFORE teardown.
        flash.SetActiveStackForTesting(null);
        vm.Poll();

        vm.FlashRunState.Should().Be("未运行");
    }

    // ---- Disabled instance -------------------------------------------------

    [Fact]
    public void CreateDisabled_Shows_Placeholders_And_Poll_Is_NoOp()
    {
        var vm = TransportParamsViewModel.CreateDisabled();

        vm.Poll(); // must not throw despite no stacks wired

        vm.DiagTransport.Should().Be("—");
        vm.DiagP2.Should().Be("—");
        vm.FlashRunState.Should().Be("未运行");
    }
}
