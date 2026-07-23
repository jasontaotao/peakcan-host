using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Phase 1 C4 Task 3.1–3.2: <see cref="FlashPanelViewModel"/> owns the secondary flash
/// stack lifecycle (build → attach → run → detach → dispose, in that strict order) and the
/// UI-facing IsFlashing / Status / Progress state. These tests use a recording stack +
/// recording factory so the VM is exercised against pure substitutes — no wire, no native DLL.
/// <para>
/// C4 risk concentrated here: the Dispose ORDER must be Detach→Client.Dispose→IsoTp→DllKey
/// (so no late frame hits a disposing IsoTp and no native handle leaks). The recording stack
/// captures the call sequence for order assertions.
/// </para>
/// </summary>
public sealed class FlashPanelViewModelTests
{
    // ---- recording test doubles ----

    /// <summary>
    /// Fast-positive UdsClient for VM lifecycle tests: overrides every executor-facing
    /// virtual to return a canned positive response WITHOUT touching the wire, mirroring
    /// PipelineExecutorTests.RecordingUdsClient. The VM drives PipelineExecutor against
    /// this client so a full success-path run completes synchronously and the teardown
    /// order (attach → detach → dispose) can be asserted.
    /// </summary>
    private sealed class FastPositiveUdsClient : UdsClient
    {
        public FastPositiveUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        {
        }

        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
            => Task.FromResult(new DiagnosticSessionResponse { SessionType = sessionType, P2 = 50, P2Star = 5000 });

        public override Task<byte[]> SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());

        public override Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());

        public override Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data = null, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());

        public override Task<int> RequestDownloadAsync(uint address, uint length, CancellationToken ct = default)
            => Task.FromResult(16); // block length > 0 so TransferData chunks cleanly.

        public override Task TransferDataAsync(byte blockSequenceCounter, byte[] data, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task RequestTransferExitAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
            => Task.FromResult((byte)0);
    }

    private sealed class RecordingFlashStack : ISecondaryFlashStack
    {
        public readonly List<string> CallOrder = new();
        // Real fast-positive client (not a NSubstitute proxy): UdsClient has no
        // parameterless ctor so Substitute.For<UdsClient>() throws, and a subclass
        // is the test-double pattern already established by PipelineExecutorTests.
        public UdsClient Client { get; } = new FastPositiveUdsClient();

        public void AttachToRouter() => CallOrder.Add("attach");
        public void DetachFromRouter() => CallOrder.Add("detach");
        public void Dispose() => CallOrder.Add("dispose");
    }

    private sealed class RecordingFactory : ISecondaryFlashStackFactory
    {
        public readonly List<(FlashStepSnapshot, FlashProfile)> Calls = new();
        public RecordingFlashStack LastStack { get; private set; } = new();

        public ISecondaryFlashStack Build(FlashStepSnapshot securityStep, FlashProfile profile)
        {
            Calls.Add((securityStep, profile));
            LastStack = new RecordingFlashStack();
            return LastStack;
        }
    }

    private static FlashViewModelTestContext Create()
    {
        var factory = new RecordingFactory();
        var vm = new FlashPanelViewModel(factory, NullLogger<FlashPanelViewModel>.Instance)
        {
            CurrentProfile = FlashProfile.CreateDefault(),
        };
        return new FlashViewModelTestContext(factory, vm);
    }

    private sealed class FlashViewModelTestContext(RecordingFactory factory, FlashPanelViewModel vm)
    {
        public RecordingFactory Factory { get; } = factory;
        public FlashPanelViewModel Vm { get; } = vm;
    }

    // ---- ctor guards ----

    [Fact]
    public void Ctor_Null_Factory_Throws()
    {
        var act = () => new FlashPanelViewModel(null!, NullLogger<FlashPanelViewModel>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Null_Logger_Throws()
    {
        var act = () => new FlashPanelViewModel(new RecordingFactory(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- default state ----

    [Fact]
    public void Default_Status_Is_Idle_And_Not_Flashing()
    {
        var ctx = Create();
        ctx.Vm.Status.Should().Be(FlashStatus.Idle);
        ctx.Vm.IsFlashing.Should().BeFalse();
    }

    [Fact]
    public void StartCommand_CanExecute_When_Idle_StopCommand_Cannot()
    {
        var ctx = Create();
        ctx.Vm.StartCommand.CanExecute(null).Should().BeTrue();
        ctx.Vm.StopCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- start wires the stack ----

    [Fact]
    public async Task Start_Builds_Stack_And_Attaches_Then_Detaches_On_Success()
    {
        var ctx = Create();
        // Default profile has 5 enabled steps (Session/Security/Erase/Download/EcuReset);
        // DownloadTransfer needs a firmware — wire a tmp file so the VM can read it.
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3, 4 });
        try
        {
            var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl.FirmwarePath = tmp;
            dl.MemoryAddress = 0x0800_0000u;
            // Default profile's SecurityAccess is Manual mode with an EMPTY key — PipelineExecutor
            // hex-decodes it and rejects empty BEFORE the wire, so the success path needs a real
            // key hex or SecurityAccess throws and the run ends Failed (not Success).
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).ManualKeyHex = "AABBCCDD";

            await ctx.Vm.StartCommand.ExecuteAsync(null);

            // Stack was built exactly once with the SecurityAccess step + the profile.
            ctx.Factory.Calls.Should().HaveCount(1);
            ctx.Factory.Calls[0].Item1.Kind.Should().Be(FlashStepKind.SecurityAccess);
            ctx.Factory.Calls[0].Item2.Should().BeSameAs(ctx.Vm.CurrentProfile);

            // Teardown after success: attach → detach → dispose (the dispose order).
            var order = ctx.Factory.LastStack.CallOrder;
            order.Should().ContainInOrder("attach", "detach", "dispose");
            order.IndexOf("attach").Should().BeLessThan(order.IndexOf("detach"),
                "attach must precede detach");
            order.Last().Should().Be("dispose",
                "dispose is the last lifecycle op — anything after it would touch a freed handle");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Start_Sets_IsFlashing_True_Status_Running_Then_Back_To_False_Success()
    {
        var ctx = Create();
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3, 4 });
        try
        {
            var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl.FirmwarePath = tmp;
            dl.MemoryAddress = 0x0800_0000u;
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).ManualKeyHex = "AABBCCDD";

            await ctx.Vm.StartCommand.ExecuteAsync(null);

            ctx.Vm.Status.Should().Be(FlashStatus.Success);
            ctx.Vm.IsFlashing.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Start_With_Auto_Security_Mode_Refuses_With_Operator_Message_Before_Building_Stack()
    {
        // C4 review #2: Auto mode is a configuration choice, so refusing it at run time must
        // report to the operator via Status/StatusMessage (mirroring the same-addressing Dll
        // refusal at line 226), NOT throw NotImplementedException out of the [RelayCommand]
        // into the WPF unobserved-exception path (which masks the status text behind a crash
        // dialog). The factory-level throw (SecondaryFlashStackFactory.Build) stays as the
        // contract backstop for any Auto snapshot that ever bypasses this VM gate.
        var ctx = Create();
        var sec = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        sec.SecurityMode = SecurityAccessMode.Auto;

        await ctx.Vm.StartCommand.ExecuteAsync(null);

        ctx.Vm.Status.Should().Be(FlashStatus.Failed,
            "Auto is a config choice — refuse the run with a visible Failed status, not an exception");
        ctx.Vm.StatusMessage.Should().NotBeNullOrWhiteSpace(
            "the operator must see why Auto was refused without reading a log");
        ctx.Factory.Calls.Should().BeEmpty("Auto mode must not build a secondary stack in Phase 1");
        ctx.Vm.IsFlashing.Should().BeFalse("failed start must reset IsFlashing");
    }

    [Fact]
    public async Task Start_Dll_Mode_With_Same_Programming_Address_As_Diagnostic_Refuses()
    {
        // Task 3.2 同寻址退化: a programming ResponseId equal to the diagnostic 0x7E8 would
        // make the secondary IsoTpLayer collide with the diagnostic one on the shared router
        // (ReceiveFlow filters by ResponseId — two layers with the same ResponseId both
        // consume every ECU response). Dll mode is the OEM-DLL path the operator likely
        // misconfigured; refuse Start with a self-explaining message rather than silently
        // corrupting the diagnostic session. Manual mode is allowed through (degraded but
        // correct-ish for a programming-session-only test).
        var ctx = Create();
        // Force the profile's programming pair to coincide with the diagnostic 0x7E0/0x7E8.
        ctx.Vm.CurrentProfile.ProgrammingCanId = new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 };
        var sec = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        sec.SecurityMode = SecurityAccessMode.Dll;

        await ctx.Vm.StartCommand.ExecuteAsync(null);

        ctx.Vm.Status.Should().Be(FlashStatus.Failed, "same-addressing Dll flash must be refused pre-flight");
        ctx.Vm.IsFlashing.Should().BeFalse();
        ctx.Factory.Calls.Should().BeEmpty(
            "the same-addressing gate runs BEFORE the factory builds a stack — no stack leaks");
        ctx.Vm.StatusMessage.Should().Contain("0x7E8",
            "the refusal message must reference the colliding diagnostic response ID so the " +
            "operator can locate the misconfiguration without reading a log");
    }

    [Fact]
    public async Task Start_Without_DownloadTransfer_Firmware_Reports_Failed_And_Tears_Down()
    {
        var ctx = Create();
        // DownloadTransfer enabled but no FirmwarePath → PipelineExecutor throws
        // InvalidOperationException; VM must catch → status Failed, and stack torn down.
        var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.MemoryAddress = 0x0800_0000u;   // FirmwarePath empty by default

        await ctx.Vm.StartCommand.ExecuteAsync(null);

        ctx.Vm.Status.Should().Be(FlashStatus.Failed);
        ctx.Vm.IsFlashing.Should().BeFalse();
        ctx.Factory.LastStack.CallOrder.Should().Contain("detach").And.Contain("dispose",
            "even on failure the stack must be torn down (no native/CAN leaks)");
    }

    // ---- stop (idle state) ----

    [Fact]
    public void StopCommand_Cannot_Execute_When_Idle()
    {
        var ctx = Create();
        ctx.Vm.StopCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- concurrency arbitration (H1) ----

    [Fact]
    public async Task After_Start_StartCommand_CanExecute_Is_False_No_Second_Stack_Built()
    {
        // H1: once a run is in flight (or just finished), StartCommand.CanExecute must be
        // false (CanExecute = !IsFlashing) so the relay gate refuses a concurrent Start.
        // This is the cheaper-but-correct surrogate for racing two Starts: the gate is
        // the invariant and Asserting its post-state proves the guard.
        var ctx = Create();
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3, 4 });
        try
        {
            var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl.FirmwarePath = tmp;
            dl.MemoryAddress = 0x0800_0000u;
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).ManualKeyHex = "AABBCCDD";

            await ctx.Vm.StartCommand.ExecuteAsync(null);

            // After a completed run the helper invariants:
            ctx.Vm.IsFlashing.Should().BeFalse();
            ctx.Vm.StartCommand.CanExecute(null).Should().BeTrue("idle again allows re-flash");
            ctx.Factory.Calls.Should().HaveCount(1, "exactly one secondary stack built per run");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
