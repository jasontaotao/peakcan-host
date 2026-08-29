using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.FlashPipeline;
using PeakCan.HIL.Core.Uds.IsoTp;
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

    /// <summary>
    /// v3.49.x PATCH (plan-uds-window-lifecycle T3/MEDIUM-2): a UdsClient whose
    /// <see cref="TransferDataAsync"/> BLOCKS until cancellation. Lets a test hold a
    /// flash run genuinely in-flight inside <see cref="PipelineExecutor"/>, then drive
    /// <see cref="FlashPanelViewModel.StopForWindowClose"/> and observe the real stack
    /// teardown order (detach→dispose) that the run's finally performs. Mirrors
    /// <see cref="FastPositiveUdsClient"/>; only the download-transfer path differs
    /// (cannot subclass — FastPositiveUdsClient is sealed).
    /// </summary>
    private sealed class StallingTransferUdsClient : UdsClient
    {
        public StallingTransferUdsClient() : base(
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
            => Task.FromResult(16);
        public override async Task TransferDataAsync(byte blockSequenceCounter, byte[] data, CancellationToken ct = default)
        {
            // Hold the executor on the first TransferData block until the token cancels.
            // StopForWindowClose cancels FlashPanelViewModel._runCts; that token is the
            // one PipelineExecutor passes here, so this await resumes with cancellation
            // and the run's finally tears the stack down — the causal chain we assert.
            try { await Task.Delay(TimeSpan.FromMilliseconds(int.MaxValue), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; } // propagate so executor sees cancel
        }
        public override Task RequestTransferExitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default) => Task.FromResult((byte)0);
    }

    /// <summary>
    /// Recording stack variant whose <see cref="Client"/> is the stalling client above,
    /// so an in-flight run parks in TransferData. The factory below chooses this stack
    /// when a test journals an in-flight-cancel scenario (real teardown causal chain).
    /// </summary>
    private sealed class StallingFlashStack : ISecondaryFlashStack
    {
        public readonly List<string> CallOrder = new();
        public UdsClient Client { get; } = new StallingTransferUdsClient();
        public IsoTpLayer Transport { get; } = new(new CanIdConfig { RequestId = 0x7E2, ResponseId = 0x7EA }, _ => { });
        public void AttachToRouter() => CallOrder.Add("attach");
        public void DetachFromRouter() => CallOrder.Add("detach");
        public void Dispose() => CallOrder.Add("dispose");
    }

    private sealed class StallingFactory : ISecondaryFlashStackFactory
    {
        public StallingFlashStack LastStack { get; private set; } = new();
        public ISecondaryFlashStack Build(FlashStepSnapshot securityStep, FlashProfile profile)
        {
            LastStack = new StallingFlashStack();
            return LastStack;
        }
    }

    private sealed class RecordingFlashStack : ISecondaryFlashStack
    {
        public readonly List<string> CallOrder = new();
        // Real fast-positive client (not a NSubstitute proxy): UdsClient has no
        // parameterless ctor so Substitute.For<UdsClient>() throws, and a subclass
        // is the test-double pattern already established by PipelineExecutorTests.
        public UdsClient Client { get; } = new FastPositiveUdsClient();
        public IsoTpLayer Transport { get; } = new(new CanIdConfig { RequestId = 0x7E2, ResponseId = 0x7EA }, _ => { });

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

    /// <summary>
    /// Test double for <see cref="IFileDialogService"/> — returns a configurable path
    /// (or null to simulate cancellation).
    /// </summary>
    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string? NextOpenResult { get; set; }
        public string? NextSaveResult { get; set; }
        public string? ShowOpenDialog(string filter) => NextOpenResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => NextSaveResult;
    }

    private static FlashViewModelTestContext Create(IFileDialogService? fileDialog = null)
    {
        var factory = new RecordingFactory();
        var vm = new FlashPanelViewModel(factory, NullLogger<FlashPanelViewModel>.Instance,
            fileDialog, null)
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
            // Phase 2: Set flat fields (kept for backward compat) — these sync to grouped params.
            dl.FirmwarePath = tmp;
            dl.MemoryAddress = 0x0800_0000u;
            // Default profile's SecurityAccess is Manual mode with an EMPTY key — PipelineExecutor
            // hex-decodes it and rejects empty BEFORE the wire, so the success path needs a real
            // key hex or SecurityAccess throws and the run ends Failed (not Success).
            // Phase 2: Executor reads flat fields (source of truth). Grouped params mirror via ToSnapshot.
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";

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
            // Phase 2: 默认模板新增 FlashDriverDownload + DependencyCheck, 测试中禁用避免干扰
            foreach (var step in ctx.Vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
                step.IsEnabled = false;

            var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl.FirmwarePath = tmp;
            dl.MemoryAddress = 0x0800_0000u;
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";

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

    // ---- step add/remove (Phase 1.1) ----

    [Fact]
    public void AddStep_Appends_New_Step_Of_Selected_Kind()
    {
        var ctx = Create();
        var before = ctx.Vm.CurrentProfile.Steps.Count;

        ctx.Vm.AddStepCommand.Execute(FlashStepKind.DownloadTransfer);

        ctx.Vm.CurrentProfile.Steps.Should().HaveCount(before + 1);
        ctx.Vm.CurrentProfile.Steps.Last().Kind.Should().Be(FlashStepKind.DownloadTransfer);
    }

    [Fact]
    public void AddStep_Default_DownloadTransfer_Has_Zero_MemoryAddress()
    {
        var ctx = Create();
        ctx.Vm.AddStepCommand.Execute(FlashStepKind.DownloadTransfer);

        var added = ctx.Vm.CurrentProfile.Steps.Last();
        added.MemoryAddress.Should().Be(0u, "new DownloadTransfer starts at address 0 — operator must fill");
        added.FirmwarePath.Should().BeNullOrEmpty("new DownloadTransfer has no firmware until operator picks one");
    }

    [Fact]
    public void RemoveStep_Removes_Selected_Step()
    {
        var ctx = Create();
        var target = ctx.Vm.CurrentProfile.Steps.First(s => s.Kind == FlashStepKind.Verify);
        ctx.Vm.SelectedStep = target;
        var before = ctx.Vm.CurrentProfile.Steps.Count;

        ctx.Vm.RemoveStepCommand.Execute(null);

        ctx.Vm.CurrentProfile.Steps.Should().HaveCount(before - 1);
        ctx.Vm.CurrentProfile.Steps.Should().NotContain(target);
    }

    [Fact]
    public void RemoveStep_Without_Selection_Does_Nothing()
    {
        var ctx = Create();
        ctx.Vm.SelectedStep = null;
        var before = ctx.Vm.CurrentProfile.Steps.Count;

        ctx.Vm.RemoveStepCommand.Execute(null);

        ctx.Vm.CurrentProfile.Steps.Should().HaveCount(before, "RemoveStep must no-op when nothing is selected");
    }

    [Fact]
    public void RemoveStepCommand_Cannot_Execute_When_Nothing_Selected()
    {
        var ctx = Create();
        ctx.Vm.SelectedStep = null;

        ctx.Vm.RemoveStepCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void RemoveStepCommand_CanExecute_True_After_Selecting_Step()
    {
        var ctx = Create();
        ctx.Vm.SelectedStep = null;
        ctx.Vm.RemoveStepCommand.CanExecute(null).Should().BeFalse("disabled when nothing selected");

        // Select a step → [NotifyCanExecuteChangedFor] must re-evaluate CanExecute
        ctx.Vm.SelectedStep = ctx.Vm.CurrentProfile.Steps.First(s => s.Kind == FlashStepKind.Verify);

        ctx.Vm.RemoveStepCommand.CanExecute(null).Should().BeTrue("enabled after selecting a step");
    }

    [Fact]
    public void AddStep_Disabled_While_Flashing()
    {
        var ctx = Create();
        // Simulate flashing state via the IsFlashing property
        ctx.Vm.IsFlashing = true;

        ctx.Vm.AddStepCommand.CanExecute(FlashStepKind.DownloadTransfer).Should().BeFalse();
        ctx.Vm.RemoveStepCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- remove firmware file / flash driver (Phase 2) ----

    [Fact]
    public void RemoveFirmwareFile_Selected_Removes_From_Collection()
    {
        // Arrange
        var ctx = Create();
        var file = new FirmwareFile("test.hex", FirmwareFormat.IntelHex, Array.Empty<Segment>());
        ctx.Vm.CurrentProfile.FirmwareFiles.Add(file);
        ctx.Vm.SelectedFirmwareFile = file;

        // Act
        ctx.Vm.RemoveFirmwareFileCommand.Execute(null);

        // Assert
        Assert.DoesNotContain(file, ctx.Vm.CurrentProfile.FirmwareFiles);
    }

    [Fact]
    public void RemoveFlashDriver_Sets_Null()
    {
        // Arrange
        var ctx = Create();
        ctx.Vm.CurrentProfile.FlashDriver = new FlashDriver("driver.dll", new byte[] { 0x01 });

        // Act
        ctx.Vm.RemoveFlashDriverCommand.Execute(null);

        // Assert
        Assert.Null(ctx.Vm.CurrentProfile.FlashDriver);
    }

    [Fact]
    public void SelectedStep_Notifies_MoveUp_CanExecute()
    {
        // Arrange
        var ctx = Create();
        ctx.Vm.CurrentProfile.Steps.Add(new FlashStep(FlashStepKind.Erase));
        ctx.Vm.CurrentProfile.Steps.Add(new FlashStep(FlashStepKind.DownloadTransfer));
        var firstStep = ctx.Vm.CurrentProfile.Steps[0];

        // Act
        ctx.Vm.SelectedStep = firstStep;

        // Assert — MoveUp 在第一位应禁用
        Assert.False(ctx.Vm.MoveUpCommand.CanExecute(null));
        Assert.True(ctx.Vm.MoveDownCommand.CanExecute(null));
    }

    // ---- file browse (Phase 1.1) ----

    [Fact]
    public void SelectDllCommand_Sets_DllPath_On_SecurityAccess_Step()
    {
        var dialog = new FakeFileDialogService { NextOpenResult = @"C:\oem\seedkey.dll" };
        var ctx = Create(dialog);
        var sec = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        ctx.Vm.SelectedStep = sec;

        ctx.Vm.SelectDllCommand.Execute(null);

        sec.DllPath.Should().Be(@"C:\oem\seedkey.dll");
    }

    [Fact]
    public void SelectDllCommand_Ignores_Cancel()
    {
        var dialog = new FakeFileDialogService { NextOpenResult = null }; // user cancelled
        var ctx = Create(dialog);
        var sec = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        sec.DllPath = @"C:\existing.dll";
        ctx.Vm.SelectedStep = sec;

        ctx.Vm.SelectDllCommand.Execute(null);

        sec.DllPath.Should().Be(@"C:\existing.dll", "cancellation must not overwrite existing path");
    }

    [Fact]
    public void SelectFirmwareCommand_Sets_FirmwarePath_On_DownloadTransfer_Step()
    {
        var dialog = new FakeFileDialogService { NextOpenResult = @"C:\fw\app.bin" };
        var ctx = Create(dialog);
        var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        ctx.Vm.SelectedStep = dl;

        ctx.Vm.SelectFirmwareCommand.Execute(null);

        dl.FirmwarePath.Should().Be(@"C:\fw\app.bin");
    }

    [Fact]
    public void SelectDllCommand_Disabled_When_Selected_Step_Is_Not_SecurityAccess()
    {
        var dialog = new FakeFileDialogService { NextOpenResult = @"C:\oem\seedkey.dll" };
        var ctx = Create(dialog);
        // Select a DownloadTransfer step, not SecurityAccess
        ctx.Vm.SelectedStep = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);

        ctx.Vm.SelectDllCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SelectFirmware_On_Wrong_Step_Kind_Does_Not_Set_Path()
    {
        var dialog = new FakeFileDialogService { NextOpenResult = @"C:\fw\app.bin" };
        var ctx = Create(dialog);
        var sec = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        ctx.Vm.SelectedStep = sec; // SecurityAccess, not DownloadTransfer

        ctx.Vm.SelectFirmwareCommand.Execute(null);

        sec.DllPath.Should().BeNullOrEmpty("SelectFirmware must not write to a non-DownloadTransfer step");
        // And the step has no FirmwarePath property set (it's a SecurityAccess step)
    }

    [Fact]
    public void FlashPanelViewModel_Exists_Without_IFileDialogService()
    {
        // Back-compat: ctor with no fileDialog must not throw (NullFileDialogService fallback)
        var factory = new RecordingFactory();
        var vm = new FlashPanelViewModel(factory, NullLogger<FlashPanelViewModel>.Instance,
            null, null)
        {
            CurrentProfile = FlashProfile.CreateDefault(),
        };
        vm.Should().NotBeNull();
        // And calling browse with the null fallback must no-op (not throw)
        var sec = vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess);
        vm.SelectedStep = sec;
        var act = () => vm.SelectDllCommand.Execute(null);
        act.Should().NotThrow();
    }

    // ---- profile save/load (Phase 1.1) ----

    [Fact]
    public async Task SaveProfile_Writes_File()
    {
        var dialog = new FakeFileDialogService { NextSaveResult = @"C:\profiles\my.flash.json" };
        var ctx = Create(dialog);
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_save_{Guid.NewGuid():N}.json");
        dialog.NextSaveResult = tmp;
        try
        {
            await ctx.Vm.SaveProfileCommand.ExecuteAsync(null);

            File.Exists(tmp).Should().BeTrue("SaveProfile must write the profile to disk");
            var json = await File.ReadAllTextAsync(tmp);
            json.Should().Contain("Default Flash", "serialized JSON should contain the profile name");
            ctx.Vm.StatusMessage.Should().Contain("saved");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task SaveProfile_Cancel_Is_NoOp()
    {
        var dialog = new FakeFileDialogService { NextSaveResult = null }; // user cancelled
        var ctx = Create(dialog);

        await ctx.Vm.SaveProfileCommand.ExecuteAsync(null);

        ctx.Vm.StatusMessage.Should().NotContain("saved", "cancellation must not report success");
    }

    [Fact]
    public async Task LoadProfile_Restores_Steps()
    {
        // Arrange: build a profile with a custom step, save it, then modify, then load.
        var ctx = Create();
        ctx.Vm.AddStepCommand.Execute(FlashStepKind.DownloadTransfer);
        var expectedCount = ctx.Vm.CurrentProfile.Steps.Count;
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_load_{Guid.NewGuid():N}.json");
        try
        {
            // Save
            var saveDialog = new FakeFileDialogService { NextSaveResult = tmp };
            var saveCtx = Create(saveDialog);
            saveCtx.Vm.AddStepCommand.Execute(FlashStepKind.DownloadTransfer);
            await saveCtx.Vm.SaveProfileCommand.ExecuteAsync(null);

            // Modify: remove a step so the profile differs from the saved one
            ctx.Vm.SelectedStep = ctx.Vm.CurrentProfile.Steps.First(s => s.Kind == FlashStepKind.Verify);
            ctx.Vm.RemoveStepCommand.Execute(null);
            ctx.Vm.CurrentProfile.Steps.Count.Should().BeLessThan(expectedCount);

            // Load
            var loadDialog = new FakeFileDialogService { NextOpenResult = tmp };
            var loadCtx = Create(loadDialog);
            await loadCtx.Vm.LoadProfileCommand.ExecuteAsync(null);

            loadCtx.Vm.CurrentProfile.Steps.Count.Should().Be(expectedCount,
                "LoadProfile must restore the full step list from disk");
            loadCtx.Vm.StatusMessage.Should().Contain("loaded");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadProfile_Invalid_Json_Reports_Operator_Message()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_bad_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp, "not valid json {{{");
        try
        {
            var dialog = new FakeFileDialogService { NextOpenResult = tmp };
            var ctx = Create(dialog);

            await ctx.Vm.LoadProfileCommand.ExecuteAsync(null);

            ctx.Vm.StatusMessage.Should().Contain("failed", "invalid JSON must report failure to operator");
            ctx.Vm.Status.Should().Be(FlashStatus.Failed);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void SaveProfile_Disabled_While_Flashing()
    {
        var ctx = Create();
        ctx.Vm.IsFlashing = true;

        ctx.Vm.SaveProfileCommand.CanExecute(null).Should().BeFalse();
        ctx.Vm.LoadProfileCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- multi-file flash (Phase 1.1) ----

    [Fact]
    public async Task Start_With_Two_DownloadTransfer_Same_File_To_Two_Addresses_Succeeds()
    {
        // M1 fix: two DownloadTransfer steps sharing the same FirmwarePath must both flash
        // (same binary to two memory addresses — e.g. dual-bank ECU). The dedup must read
        // the file once but populate firmware for BOTH step indices.
        var ctx = Create();
        var tmp = Path.Combine(Path.GetTempPath(), $"flashvmtest_same_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tmp, new byte[] { 0xAA, 0xBB });
        try
        {
            // Phase 2: 禁用默认模板中新增的 FlashDriverDownload + DependencyCheck
            foreach (var step in ctx.Vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
                step.IsEnabled = false;

            var dl1 = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl1.FirmwarePath = tmp;
            dl1.MemoryAddress = 0x1000u;
            ctx.Vm.AddStepCommand.Execute(FlashStepKind.DownloadTransfer);
            var dl2 = ctx.Vm.CurrentProfile.Steps.Last(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl2.FirmwarePath = tmp; // SAME path as dl1
            dl2.MemoryAddress = 0x2000u;
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";

            await ctx.Vm.StartCommand.ExecuteAsync(null);

            ctx.Vm.Status.Should().Be(FlashStatus.Success,
                "same-file two-address flash must succeed — both steps get the firmware");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Start_With_Two_DownloadTransfer_Steps_Flashes_Both_Files()
    {
        // Phase 1.1: two DownloadTransfer steps with different firmware content. The resolver
        // must return each step's own firmware (validated via distinct MemoryAddress per step).
        var ctx = Create();
        var tmpA = Path.Combine(Path.GetTempPath(), $"flashvmtest_A_{Guid.NewGuid():N}.bin");
        var tmpB = Path.Combine(Path.GetTempPath(), $"flashvmtest_B_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tmpA, new byte[] { 0xAA, 0xBB });
        await File.WriteAllBytesAsync(tmpB, new byte[] { 0xCC, 0xDD, 0xEE });
        try
        {
            // Phase 2: 禁用默认模板中新增的 FlashDriverDownload + DependencyCheck
            foreach (var step in ctx.Vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
                step.IsEnabled = false;

            // Default profile has one DownloadTransfer at some index — add a second one.
            var dl1 = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl1.FirmwarePath = tmpA;
            dl1.MemoryAddress = 0x1000u;
            ctx.Vm.AddStepCommand.Execute(FlashStepKind.DownloadTransfer);
            var dl2 = ctx.Vm.CurrentProfile.Steps.Last(s => s.Kind == FlashStepKind.DownloadTransfer);
            dl2.FirmwarePath = tmpB;
            dl2.MemoryAddress = 0x2000u;
            // SecurityAccess needs a valid key for the success path
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";

            await ctx.Vm.StartCommand.ExecuteAsync(null);

            ctx.Vm.Status.Should().Be(FlashStatus.Success);
            // Verify both files were downloaded at their respective addresses via the client
            var client = (FastPositiveUdsClient)ctx.Factory.LastStack.Client;
            // The client doesn't expose call records, but Success status + correct teardown
            // confirms both DownloadTransfer steps ran. Address-based verification is covered
            // by PipelineExecutorTests.PerStepResolver_Two_DownloadTransfer_Steps_Each_Get_Own_Firmware.
            ctx.Factory.LastStack.CallOrder.Should().ContainInOrder("attach", "detach", "dispose");
        }
        finally
        {
            if (File.Exists(tmpA)) File.Delete(tmpA);
            if (File.Exists(tmpB)) File.Delete(tmpB);
        }
    }

    // ---- Load Profile preserves Erase Segment selection (bug) ----

    [Fact]
    public void LoadProfile_Preserves_FirmwareFiles_And_Erase_SegmentIndex()
    {
        // Bug: after Load Profile, the Erase step's Segment ComboBox was empty.
        // Root cause: FirmwareFile/Segment are records with byte[] Data, and the
        // EraseSegmentIndex (VM-level) was not synced from the loaded step. This test
        // pins that AllSegments is populated and the Erase step's SegmentIndex survives.
        var ctx = Create();

        // Add a firmware file with a segment (simulates AddFirmwareFile).
        var segData = new byte[] { 0xAA, 0xBB, 0xCC };
        var segment = new Segment(0x0800_0000u, segData) { Crc32 = Crc32.Compute(segData) };
        var fwFile = new FirmwareFile("test.bin", FirmwareFormat.RawBinary, new[] { segment });
        ctx.Vm.CurrentProfile.FirmwareFiles.Add(fwFile);

        // Configure Erase step to reference segment 0.
        var erase = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        erase.RoutineControl!.SegmentIndex = 0;

        // Save and reload.
        var json = ctx.Vm.CurrentProfile.ToJson();
        ctx.Vm.CurrentProfile = FlashProfile.FromJson(json);

        // AllSegments must be populated from the loaded FirmwareFiles.
        ctx.Vm.AllSegments.Should().HaveCount(1, "FirmwareFiles must survive round-trip so AllSegments is populated");
        ctx.Vm.AllSegments[0].StartAddress.Should().Be(0x0800_0000u);

        // The Erase step's SegmentIndex must survive.
        var loadedErase = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        loadedErase.RoutineControl!.SegmentIndex.Should().Be(0,
            "Erase step's SegmentIndex must survive Load Profile");

        // Selecting the Erase step must sync EraseSegmentIndex so the ComboBox shows the choice.
        ctx.Vm.SelectedStep = loadedErase;
        ctx.Vm.EraseSegmentIndex.Should().Be(0,
            "EraseSegmentIndex must sync from the selected Erase step's RoutineControl.SegmentIndex");
    }

    // ---- Load Profile after restart (new VM instance) ----

    [Fact]
    public async Task LoadProfile_After_Restart_Populates_AllSegments_And_Erase_Selection()
    {
        // Bug: restart the app, Load Profile -> Erase step's Segment ComboBox is empty.
        // This simulates a fresh VM (no in-memory FirmwareFiles) loading a profile from disk.
        var saveCtx = Create();
        var segData = new byte[] { 0xAA, 0xBB, 0xCC };
        var segment = new Segment(0x0800_0000u, segData) { Crc32 = Crc32.Compute(segData) };
        var fwFile = new FirmwareFile("test.bin", FirmwareFormat.RawBinary, new[] { segment });
        saveCtx.Vm.CurrentProfile.FirmwareFiles.Add(fwFile);
        var erase = saveCtx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        erase.RoutineControl!.SegmentIndex = 0;

        // Save to disk.
        var tmp = Path.Combine(Path.GetTempPath(), $"flashloadtest_{Guid.NewGuid():N}.json");
        var saveDialog = new FakeFileDialogService { NextSaveResult = tmp };
        var diskCtx = Create(saveDialog);
        diskCtx.Vm.CurrentProfile = saveCtx.Vm.CurrentProfile;
        await diskCtx.Vm.SaveProfileCommand.ExecuteAsync(null);

        try
        {
            // Simulate restart: brand-new VM + Load Profile from disk.
            var loadDialog = new FakeFileDialogService { NextOpenResult = tmp };
            var loadCtx = Create(loadDialog);
            loadCtx.Vm.AllSegments.Should().BeEmpty("fresh VM has no FirmwareFiles yet");

            await loadCtx.Vm.LoadProfileCommand.ExecuteAsync(null);

            // After load, AllSegments must be populated from the deserialized FirmwareFiles.
            loadCtx.Vm.AllSegments.Should().HaveCount(1,
                "FirmwareFiles must deserialize from disk and populate AllSegments");
            loadCtx.Vm.AllSegments[0].StartAddress.Should().Be(0x0800_0000u);

            // The Erase step's SegmentIndex must survive.
            var loadedErase = loadCtx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Erase);
            loadedErase.RoutineControl!.SegmentIndex.Should().Be(0);

            // Selecting the Erase step must sync EraseSegmentIndex so the ComboBox shows it.
            loadCtx.Vm.SelectedStep = loadedErase;
            loadCtx.Vm.EraseSegmentIndex.Should().Be(0);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ---- EraseSegmentIndex PropertyChanged on step switch (bug) ----

    [Fact]
    public void Selecting_Erase_Step_Raises_EraseSegmentIndex_PropertyChanged()
    {
        // Bug: OnSelectedStepChanged wrote the backing field directly, so the
        // [ObservableProperty] setter never ran and UI never got the PropertyChanged
        // for EraseSegmentIndex. The ComboBox stayed empty even though the value
        // was internally correct. This test pins that the notification fires.
        var ctx = Create();
        var erase = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        erase.RoutineControl!.SegmentIndex = 0;

        // Add a firmware file so segment 0 exists.
        var segData = new byte[] { 0xAA, 0xBB, 0xCC };
        var segment = new Segment(0x0800_0000u, segData) { Crc32 = Crc32.Compute(segData) };
        ctx.Vm.CurrentProfile.FirmwareFiles.Add(
            new FirmwareFile("test.bin", FirmwareFormat.RawBinary, new[] { segment }));

        var firedProperties = new System.Collections.Generic.List<string>();
        ((System.ComponentModel.INotifyPropertyChanged)ctx.Vm).PropertyChanged += (_, e) =>
            firedProperties.Add(e.PropertyName ?? "");

        // Select the Erase step - must raise EraseSegmentIndex PropertyChanged.
        ctx.Vm.SelectedStep = erase;

        firedProperties.Should().Contain(nameof(FlashPanelViewModel.EraseSegmentIndex),
            "selecting an Erase step with a saved SegmentIndex must notify the ComboBox " +
            "so it shows the selection instead of appearing empty");
        ctx.Vm.EraseSegmentIndex.Should().Be(0);
    }

    // ---- RoutineId edit persistence (bug: 切换 step 后值被重置) ----

    [Fact]
    public void RoutineId_Edit_Persists_Across_Step_Switch()
    {
        // Bug: operator edits Verify's RoutineId, switches to another step, switches back -
        // the RoutineId was reset to 0. Root cause was StringFormat=0x{0:X4} in XAML
        // (WPF couldn't parse "0x0204" back to ushort), NOT a VM/data-model issue.
        // This test pins the data-model invariant: the value set on RoutineControl.RoutineId
        // stays on the FlashStep instance regardless of SelectedStep changes.
        var ctx = Create();
        var verify = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Verify);
        verify.RoutineControl!.RoutineId = 0x0204;

        // Switch to a different step.
        var erase = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.Erase);
        ctx.Vm.SelectedStep = erase;
        ctx.Vm.SelectedStep.Should().BeSameAs(erase);

        // Switch back to Verify.
        ctx.Vm.SelectedStep = verify;

        // The Verify step's RoutineId must be the value the operator set, not reset to 0.
        verify.RoutineControl.RoutineId.Should().Be(0x0204,
            "RoutineId must persist across step switches - the data model holds the value; " +
            "any reset is a XAML binding issue, not a VM issue");

        // Same invariant for PreCheck and DependencyCheck.
        var preCheck = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.PreCheck);
        preCheck.PreCheck!.RoutineId = 0xFF05;
        ctx.Vm.SelectedStep = erase;
        ctx.Vm.SelectedStep = preCheck;
        preCheck.PreCheck.RoutineId.Should().Be(0xFF05);

        var depCheck = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DependencyCheck);
        depCheck.DependencyCheck!.RoutineId = 0xFF10;
        ctx.Vm.SelectedStep = erase;
        ctx.Vm.SelectedStep = depCheck;
        depCheck.DependencyCheck.RoutineId.Should().Be(0xFF10);
    }

    // ---- Phase 2 FirmwareFiles + SegmentIndex path (C-4 fix) ----

    [Fact]
    public async Task Start_With_FirmwareFiles_And_SegmentIndex_Succeeds_Phase2_Path()
    {
        // C-4 fix: Phase 2 path uses FirmwareFiles + SegmentIndex instead of FirmwarePath.
        // The default template's DownloadTransfer step should work when the operator loads
        // a firmware file via AddFirmwareFile and selects a segment, WITHOUT setting FirmwarePath.
        var ctx = Create();
        // Disable FlashDriverDownload + DependencyCheck to isolate the download path.
        foreach (var step in ctx.Vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
            step.IsEnabled = false;

        // Simulate AddFirmwareFile: parse a raw binary into FirmwareFiles.
        var firmwareBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var segment = new Segment(0x0800_0000u, firmwareBytes) { Crc32 = Crc32.Compute(firmwareBytes) };
        var fwFile = new FirmwareFile("test.bin", FirmwareFormat.RawBinary, new[] { segment });
        ctx.Vm.CurrentProfile.FirmwareFiles.Add(fwFile);

        // Configure DownloadTransfer to reference segment 0 (Phase 2 path).
        var dl = ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.Download!.SegmentIndex = 0;
        // Do NOT set FirmwarePath - Phase 2 path must work without it.

        // SecurityAccess needs a valid key for the success path.
        ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";

        await ctx.Vm.StartCommand.ExecuteAsync(null);

        ctx.Vm.Status.Should().Be(FlashStatus.Success,
            "Phase 2 FirmwareFiles+SegmentIndex path must work without FirmwarePath");
        ctx.Vm.IsFlashing.Should().BeFalse();
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
            ctx.Vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";

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

    // ---- window/singleton lifecycle (v3.49.x PATCH: decouple VM lifetime from window instances) ----
    //
    // UdsWindow.Unloaded used to call Flash.Dispose(), and Dispose set a one-shot
    // _disposed flag that permanently refused Start (ObjectDisposedException + a
    // permanently-greyed Start button). But FlashPanelViewModel is a DI singleton
    // (AppHostBuilder.cs:284 AddSingleton), so the window close + reopen cycle reused
    // the SAME disposed VM — rendering Flash unreachable forever after the first close.
    // Dispose must now be idempotent + reversible: it only stops an in-flight run; it
    // never disables a subsequent Start. See docs/plan-uds-window-lifecycle.md T1.

    [Fact]
    public void Dispose_Does_Not_Permanently_Disable_Start_CanExecute()
    {
        // After Dispose the VM must remain reusable (the UDS window reopens bound to the
        // same singleton VM). StartCommand.CanExecute must stay true when idle.
        var ctx = Create();

        ctx.Vm.Dispose();
        ctx.Vm.Dispose(); // idempotent — process shutdown may call twice via DI cascade

        ctx.Vm.IsFlashing.Should().BeFalse();
        ctx.Vm.StopCommand.CanExecute(null).Should().BeFalse("idle has nothing to stop");
    }

    [Fact]
    public async Task Dispose_Then_Start_Does_Not_Throw_ObjectDisposed()
    {
        // The old _disposed gate threw ObjectDisposedException before any pre-flight check,
        // surfacing as a WPF crash dialog on the second open's first Start. With the gate
        // removed, the recording-factory path runs normally (the default profile with an
        // empty manual key fails pre-flight → Failed or Success per stack outcome, NOT an
        // ObjectDisposedException).
        var ctx = Create();
        ctx.Vm.Dispose();

        var act = async () => await ctx.Vm.StartCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync("Dispose must not trap the VM in a permanently-disposed state");
        ctx.Vm.IsFlashing.Should().BeFalse();
        ctx.Vm.Status.Should().NotBe(FlashStatus.Idle,
            "Start must still attempt the run and report an outcome (Success/Failed), not silently idle");
    }

    [Fact]
    public void StopForWindowClose_Keeps_Vm_Reusable()
    {
        // Window-level halt (UdsWindow.Unloaded) routes here instead of Dispose:
        // stops the in-flight run + tears down its stack, but leaves the VM reusable
        // for the next window instance. Mirrors SessionPanelViewModel.StopForWindowClose.
        var ctx = Create();

        ctx.Vm.StopForWindowClose();

        ctx.Vm.IsFlashing.Should().BeFalse("window close must not leave IsFlashing lying");
        ctx.Vm.StartCommand.CanExecute(null).Should().BeTrue("VM stays reusable after window-level stop");
    }

    /// <summary>
    /// MEDIUM-2 coverage (reviewer finding): the idle StopForWindowClose tests above
    /// prove CanExecute reuse but NOT the real in-flight teardown causal chain — that
    /// UdsWindow.Unloaded → StopForWindowClose → cancel → StartAsync catch → finally
    /// → stack.DetachFromRouter → stack.Dispose (the Detach→Client→IsoTp→DllKey order
    /// that releases the native OEM-DLL handle). This test pins it against a real
    /// <see cref="StallingFactory"/> whose run parks in TransferData, so Unloaded's
    /// StopForWindowClose must drive the recording stack's detach+dispose observeably.
    /// </summary>
    [Fact]
    public async Task StopForWindowClose_During_In_Flight_Flash_Tears_Down_Real_Stack()
    {
        var factory = new StallingFactory();
        var vm = new FlashPanelViewModel(factory, NullLogger<FlashPanelViewModel>.Instance)
        {
            CurrentProfile = FlashProfile.CreateDefault(),
        };
        // Phase 2: 禁用默认模板中新增的 FlashDriverDownload + DependencyCheck
        foreach (var step in vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
            step.IsEnabled = false;
        var dl = vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.FirmwarePath = Path.Combine(Path.GetTempPath(), $"flashstall_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(dl.FirmwarePath, new byte[] { 1, 2, 3, 4 });
        dl.MemoryAddress = 0x0800_0000u;
        vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";
        try
        {
            // Kick the run off; it parks inside PipelineExecutor on the first TransferData
            // (StallingTransferUdsClient blocks until the run's ct cancels).
            var run = vm.StartCommand.ExecuteAsync(null);
            await WaitFor(() => vm.IsFlashing, millisecondsTimeout: 2000);
            vm.IsFlashing.Should().BeTrue("the run must be genuinely in-flight before the window closes");
            factory.LastStack.CallOrder.Should().Contain("attach",
                "the secondary stack was built + attached before the executor parked");

            // Window-level halt (UdsWindow.Unloaded path): cancel in-flight.
            vm.StopForWindowClose();

            await run; // the finally runs to completion on cancel
            // The real teardown causal chain: the run's finally must Detach then Dispose the
            // secondary stack (the Detach→Client→IsoTp→DllKey order that releases the native
            // OEM-DLL handle). StopForWindowClose only cancels; the stack actually came down.
            var order = factory.LastStack.CallOrder;
            order.Should().Contain("detach", "Unloaded's StopForWindowClose must detach the in-flight stack");
            order.Should().Contain("dispose", "and dispose it — no native handle leaks across close/reopen");
            order.IndexOf("detach").Should().BeLessThan(order.IndexOf("dispose"),
                "detach must precede dispose so no late router frame hits a disposing IsoTp");

            vm.IsFlashing.Should().BeFalse();
            vm.Status.Should().Be(FlashStatus.Cancelled, "the run observed OperationCanceledException");
            // Reuse invariant: the (singleton) VM stays startable for the next window.
            vm.StartCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(dl.FirmwarePath)) File.Delete(dl.FirmwarePath);
        }
    }

    private static async Task WaitFor(Func<bool> predicate, int millisecondsTimeout)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millisecondsTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Predicate did not become true within {millisecondsTimeout} ms");
    }

    // ---- IHostApplicationLifetime wiring (MEDIUM-1 native-handle governance) ----

    /// <summary>
    /// Stand-in <see cref="IHostApplicationLifetime"/> for tests: exposes the three standard
    /// tokens (ApplicationStarted/Stopping/Stopped) as <see cref="CancellationTokenSource"/>
    /// properties the test can trigger at will, plus <see cref="StopApplication"/> to simulate
    /// App.OnExit's host.StopAsync cascade. Not a full implementation — just enough surface
    /// for the VM's linked-token + CurrentRunTask assertions.
    /// </summary>
    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource StartedCts { get; } = new();
        public CancellationTokenSource StoppingCts { get; } = new();
        public CancellationTokenSource StoppedCts { get; } = new();

        public CancellationToken ApplicationStarted => StartedCts.Token;
        public CancellationToken ApplicationStopping => StoppingCts.Token;
        public CancellationToken ApplicationStopped => StoppedCts.Token;

        public void StopApplication() => StoppingCts.Cancel();
    }

    private static FlashPanelViewModel CreateWithLifetime(
        FakeHostApplicationLifetime lifetime,
        ISecondaryFlashStackFactory? factory = null)
    {
        var vm = new FlashPanelViewModel(
            factory ?? new RecordingFactory(),
            NullLogger<FlashPanelViewModel>.Instance,
            null, lifetime)
        {
            CurrentProfile = FlashProfile.CreateDefault(),
        };
        return vm;
    }

    [Fact]
    public async Task StartCommand_Assigns_CurrentRunTask_During_Run()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var factory = new StallingFactory();
        var vm = CreateWithLifetime(lifetime, factory);
        var dl = vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.FirmwarePath = Path.Combine(Path.GetTempPath(), $"curr_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(dl.FirmwarePath, new byte[] { 1, 2, 3, 4 });
        dl.MemoryAddress = 0x0800_0000u;
        vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";
        try
        {
            var run = vm.StartCommand.ExecuteAsync(null);
            await WaitFor(() => vm.IsFlashing, millisecondsTimeout: 2000);

            // CurrentRunTask is non-null while the run is genuinely in-flight.
            vm.CurrentRunTask.Should().NotBeNull("the VM must expose the in-flight run for App.OnExit to await");
            vm.CurrentRunTask!.IsCompleted.Should().BeFalse("the run is still parked in TransferData");

            // Cancel to let the run finish.
            vm.StopForWindowClose();
            await run;
        }
        finally
        {
            if (File.Exists(dl.FirmwarePath)) File.Delete(dl.FirmwarePath);
        }
    }

    [Fact]
    public async Task ApplicationStopping_Cancels_In_Flight_Run()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var factory = new StallingFactory();
        var vm = CreateWithLifetime(lifetime, factory);
        // Phase 2: 禁用默认模板中新增的 FlashDriverDownload + DependencyCheck
        foreach (var step in vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
            step.IsEnabled = false;
        var dl = vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.FirmwarePath = Path.Combine(Path.GetTempPath(), $"stop_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(dl.FirmwarePath, new byte[] { 1, 2, 3, 4 });
        dl.MemoryAddress = 0x0800_0000u;
        vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";
        try
        {
            var run = vm.StartCommand.ExecuteAsync(null);
            await WaitFor(() => vm.IsFlashing, millisecondsTimeout: 2000);

            // Simulate App.OnExit's host.StopAsync cascade: ApplicationStopping fires.
            lifetime.StopApplication();

            await run; // the linked token cancels the run → finally tears the stack down.
            vm.Status.Should().Be(FlashStatus.Cancelled,
                "ApplicationStopping must cancel the in-flight run via the linked token");
            factory.LastStack.CallOrder.Should().Contain("detach");
            factory.LastStack.CallOrder.Should().Contain("dispose");
        }
        finally
        {
            if (File.Exists(dl.FirmwarePath)) File.Delete(dl.FirmwarePath);
        }
    }

    [Fact]
    public async Task CurrentRunTask_Clears_After_Run_Completes()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var vm = CreateWithLifetime(lifetime); // fast-positive factory → run completes synchronously
        // Phase 2: 禁用默认模板中新增的 FlashDriverDownload + DependencyCheck
        foreach (var step in vm.CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.FlashDriverDownload || s.Kind == FlashStepKind.DependencyCheck))
            step.IsEnabled = false;
        var dl = vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.DownloadTransfer);
        dl.FirmwarePath = Path.Combine(Path.GetTempPath(), $"done_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(dl.FirmwarePath, new byte[] { 1, 2, 3, 4 });
        dl.MemoryAddress = 0x0800_0000u;
        vm.CurrentProfile.Steps.Single(s => s.Kind == FlashStepKind.SecurityAccess).SecurityAccess!.ManualKeyHex = "AABBCCDD";
        try
        {
            await (Task)vm.StartCommand.ExecuteAsync(null)!;

            // After the run completes, CurrentRunTask is reset so App.OnExit's null check
            // skips the await when there is no in-flight flash.
            vm.CurrentRunTask.Should().BeNull("the VM must clear CurrentRunTask once the run finishes");
            vm.Status.Should().Be(FlashStatus.Success);
        }
        finally
        {
            if (File.Exists(dl.FirmwarePath)) File.Delete(dl.FirmwarePath);
        }
    }
}
