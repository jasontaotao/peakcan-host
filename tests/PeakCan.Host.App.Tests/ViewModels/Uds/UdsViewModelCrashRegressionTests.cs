using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// v2.0.6 PATCH Bug-3 regression tests: when the SDK throws (no ECU
/// connected, P2 timeout, etc.) the RelayCommand entry point in each
/// UDS VM must
///   1. NOT propagate the exception out of the command (caller / WPF
///      dispatcher would surface it as an unhandled exception → crash),
///   2. leave the row's "IsReading" / "Status" flag in the steady-state
///      value so subsequent reads aren't silently locked out,
///   3. log the failure to the shared OutputLog so the operator sees
///      what happened.
///
/// Pre-v2.0.6 the catch / finally blocks ran on the threadpool because
/// of <c>ConfigureAwait(false)</c> on the awaited <c>UdsClient</c> call.
/// Combined with WPF's cross-thread binding checks (ObservableCollection
/// mutation, ObservableProperty setter, RelayCommand
/// NotifyCanExecuteChanged), this manifested as "program hangs and
/// crashes" per the v2.0.6 bug report. The fix is to remove
/// ConfigureAwait(false) from VM awaits so the WPF SynchronizationContext
/// keeps the continuation on the UI dispatcher; these tests pin the
/// observable behavior that the fix preserves.
/// </summary>
public sealed class UdsViewModelCrashRegressionTests
{
    /// <summary>Fake UdsClient that throws a representative SDK exception for any operation.</summary>
    private sealed class AlwaysThrowingUdsClient : UdsClient
    {
        public int CallCount;

        public AlwaysThrowingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            // Simulates a P2 timeout / no-ECU "channel not initialized" type failure.
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }

        public override Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }

        public override Task<byte[]> ReadDtcInformationAsync(byte subFunction, byte statusMask, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }

        public override Task ClearDiagnosticInformationAsync(uint groupOfDtc = 0xFFFFFF, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }

        public override Task<byte[]> RoutineControlAsync(byte subFunction, ushort routineIdentifier, byte[]? data = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }

        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }

        public override Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("PCAN_ERROR_INITIALIZE: channel not initialized");
        }
    }

    private static DidDatabase NewDidDb() =>
        new(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);

    private static RoutineDatabase NewRoutineDb() =>
        new(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance);

    [Fact]
    public async Task ReadDidCommand_With_NoEcu_Exception_DoesNotCrash_AndClearsIsReading()
    {
        var fake = new AlwaysThrowingUdsClient();
        var vm = new DidPanelViewModel(fake, NewDidDb());
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        // The bug-3 crash was: this call would propagate the SDK exception
        // out of the command (UI thread unhandled exception → process
        // crash) or leave IsReading stuck at true if the catch handler
        // somehow ran. Pin both invariants.
        var act = async () => await vm.ReadDidCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync("ReadDidCommand must swallow SDK exceptions and surface them via the log");
        vm.SelectedDid.Should().NotBeNull();
        vm.SelectedDid!.IsReading.Should().BeFalse("the finally block must always reset IsReading");
        log.Should().Contain(l => l.Level == "Error" && l.Message.Contains("0xF190", StringComparison.OrdinalIgnoreCase),
            "the catch handler must log the failure with the DID id so operators can see what failed");
    }

    [Fact]
    public async Task WriteDidCommand_With_NoEcu_Exception_DoesNotCrash()
    {
        var fake = new AlwaysThrowingUdsClient();
        var vm = new DidPanelViewModel(fake, NewDidDb());
        vm.WriteValue = "DEADBEEF";

        var act = async () => await vm.WriteDidCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReadDtcsCommand_With_NoEcu_Exception_DoesNotCrash()
    {
        var fake = new AlwaysThrowingUdsClient();
        var vm = new DtcPanelViewModel(fake);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        var act = async () => await vm.ReadDtcsCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        log.Should().Contain(l => l.Level == "Error");
    }

    [Fact]
    public async Task ClearDtcsCommand_With_NoEcu_Exception_DoesNotCrash()
    {
        var fake = new AlwaysThrowingUdsClient();
        var vm = new DtcPanelViewModel(fake);

        var act = async () => await vm.ClearDtcsCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartRoutineCommand_With_NoEcu_Exception_DoesNotCrash_AndSetsStatusFailed()
    {
        var fake = new AlwaysThrowingUdsClient();
        // Use the built-in defaults from the db so a routine is selected.
        var vm = new RoutinePanelViewModel(fake, NewRoutineDb());
        // RoutineDatabase built-in defaults are empty — seed a row directly.
        vm.Routines.Add(new PeakCan.Host.App.ViewModels.Uds.Rows.RoutineRow
        {
            Id = 0xFF00, Name = "TestRoutine", Status = "Idle"
        });
        vm.SelectedRoutine = vm.Routines[0];

        var act = async () => await vm.StartRoutineCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        vm.SelectedRoutine!.Status.Should().Be("Failed", "the catch block must reset Status so CanExecute flips back on");
    }

    [Fact]
    public async Task SetDefaultSessionCommand_With_NoEcu_Exception_DoesNotCrash()
    {
        var fake = new AlwaysThrowingUdsClient();
        var vm = new SessionPanelViewModel(fake, NullLogger<SessionPanelViewModel>.Instance);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        var before = vm.CurrentSession;

        var act = async () => await vm.SetDefaultSessionCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        log.Should().Contain(l => l.Level == "Error");
        // Bug-3 secondary symptom: CurrentSession must NOT have updated
        // when the underlying DiagnosticSessionControl call threw.
        // Compare against the captured before-state (not a literal —
        // CurrentSession's ctor default is already "Default").
        vm.CurrentSession.Should().Be(before, "a failed session change must leave CurrentSession at its prior value");
    }

    [Fact]
    public async Task SecurityAccessCommand_With_NoEcu_Exception_DoesNotCrash_AndClearsSecurityLevel()
    {
        var fake = new AlwaysThrowingUdsClient();
        var vm = new SessionPanelViewModel(fake, NullLogger<SessionPanelViewModel>.Instance);

        var act = async () => await vm.SecurityAccessCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        vm.SecurityLevel.Should().BeNull("the generic catch handler must clear SecurityLevel on failure");
    }
}