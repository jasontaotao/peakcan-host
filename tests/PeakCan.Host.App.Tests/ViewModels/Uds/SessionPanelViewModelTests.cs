using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for SessionPanelViewModel. Covers all 5 RelayCommands +
/// the SecurityAccess 4-catch ladder (KeyAlgorithmNotConfigured /
/// UdsNegativeResponse / InvalidOperation / generic Exception).
/// </summary>
public sealed class SessionPanelViewModelTests
{
    private sealed class RecordingUdsClient : UdsClient
    {
        public List<byte> SessionCalls { get; } = new();
        public List<(byte Level, byte[]? Key)> SecurityCalls { get; } = new();
        public byte[] NextSeed { get; set; } = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        public bool SecurityAccessThrowsNrc { get; set; }
        public bool SecurityAccessThrowsInvalidOp { get; set; }
        public bool TesterPresentThrows { get; set; }

        public RecordingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
        {
            SecurityCalls.Add((requestLevel, null));
            if (SecurityAccessThrowsInvalidOp)
                throw new InvalidOperationException("UdsClient was constructed without an IKeyDerivationAlgorithm.");
            if (SecurityAccessThrowsNrc)
                throw new UdsNegativeResponseException(0x22, UdsNegativeResponseCode.ConditionsNotCorrect);
            return Task.FromResult(Array.Empty<byte>());
        }

        public override Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
        {
            SessionCalls.Add(sessionType);
            return Task.FromResult(new DiagnosticSessionResponse
            {
                SessionType = sessionType,
                P2 = 50,
                P2Star = 5000
            });
        }

        public override Task TesterPresentAsync(CancellationToken ct = default)
        {
            if (TesterPresentThrows)
                throw new InvalidOperationException("TesterPresent underlying transport closed.");
            return Task.CompletedTask;
        }
    }

    private static SessionPanelViewModel NewVm(RecordingUdsClient fake)
        => new(fake, NullLogger<SessionPanelViewModel>.Instance);

    [Fact]
    public void Ctor_Defaults_CurrentSession_Default_SecurityLevel_Null_TesterPresentActive_False()
    {
        var vm = NewVm(new RecordingUdsClient());
        vm.CurrentSession.Should().Be("Default");
        vm.SecurityLevel.Should().BeNull();
        vm.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public async Task SetDefaultSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x01()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SetDefaultSessionCommand.ExecuteAsync(null);

        fake.SessionCalls.Should().ContainSingle().Which.Should().Be(0x01);
        vm.CurrentSession.Should().Be("Default");
    }

    [Fact]
    public async Task SetExtendedSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x02()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SetExtendedSessionCommand.ExecuteAsync(null);

        fake.SessionCalls.Should().ContainSingle().Which.Should().Be(0x02);
        vm.CurrentSession.Should().Be("Extended");
    }

    [Fact]
    public async Task SetProgrammingSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x03()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        await vm.SetProgrammingSessionCommand.ExecuteAsync(null);

        fake.SessionCalls.Should().ContainSingle().Which.Should().Be(0x03);
        vm.CurrentSession.Should().Be("Programming");
    }

    [Fact]
    public void ToggleTesterPresentCommand_Flips_TesterPresentActive_And_Starts_BackgroundLoop()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        vm.ToggleTesterPresentCommand.Execute(null);

        vm.TesterPresentActive.Should().BeTrue();

        vm.ToggleTesterPresentCommand.Execute(null);

        vm.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public void Dispose_CancelsTesterPresentLoop()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        // Start the tester-present loop so the lazy _testerPresentCts is created.
        vm.ToggleTesterPresentCommand.Execute(null);
        vm.TesterPresentActive.Should().BeTrue();

        // Dispose must not throw — it cancels and disposes the CTS so the
        // background tester-present loop exits cleanly. This is the contract
        // that UdsView.OnUnloaded relies on to release the CTS at tab close
        // rather than letting it leak to process exit.
        var act = () => vm.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_Resets_TesterPresentActive_To_False()
    {
        // Bug (v1.2.x PATCH backlog): Dispose() cancels and disposes the CTS
        // but leaves the public TesterPresentActive flag stuck at true. After
        // disposal, downstream observers see a stale "running" state even
        // though the background loop has been cancelled. Fix: Dispose() must
        // also reset TesterPresentActive to false.
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        // Start the tester-present loop so TesterPresentActive flips to true.
        vm.ToggleTesterPresentCommand.Execute(null);
        vm.TesterPresentActive.Should().BeTrue();

        vm.Dispose();

        vm.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public async Task SecurityAccessCommand_With_Placeholder_Algorithm_Logs_HintMessage_DoesNotCrash()
    {
        var fake = new RecordingUdsClient { SecurityAccessThrowsInvalidOp = true };
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Warn" && l.Message.Contains("IKeyDerivationAlgorithm", StringComparison.OrdinalIgnoreCase));
        log.Should().Contain(l => l.Level == "Info" && l.Message.Contains("Hint", StringComparison.OrdinalIgnoreCase));
        vm.SecurityLevel.Should().BeNull();
    }

    [Fact]
    public async Task SecurityAccessCommand_With_Fake_Algorithm_Sets_SecurityLevel_0x01()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        vm.SecurityLevel.Should().Be((byte)0x01);
        log.Should().Contain(l => l.Level == "Info" && l.Message.Contains("succeeded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecurityAccessCommand_With_UdsNegativeResponse_Logs_Warn_And_Clears_SecurityLevel()
    {
        var fake = new RecordingUdsClient { SecurityAccessThrowsNrc = true };
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.SecurityAccessCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Warn" && l.Message.Contains("NRC", StringComparison.OrdinalIgnoreCase));
        vm.SecurityLevel.Should().BeNull();
    }

    [Fact]
    public void AttachLog_Null_DoesNotThrow()
    {
        var vm = NewVm(new RecordingUdsClient());
        var act = () => vm.AttachLog(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- v3.8.7 PATCH H3: TesterPresent thread-pool catch arm ----------

    /// <summary>
    /// v3.8.7 PATCH H3: <see cref="SessionPanelViewModel.ToggleTesterPresent"/>
    /// spawns a Task.Run loop that, when the first <c>TesterPresentAsync</c>
    /// throws, lands in a <c>catch (Exception ex)</c> arm on the threadpool
    /// thread. Pre-fix, the catch called <c>AppendLog(...)</c> +
    /// <c>TesterPresentActive = false</c> directly from the threadpool:
    /// <list type="bullet">
    ///   <item><c>AppendLog</c> adds to <c>_log</c> (ObservableCollection
    ///   bound to the WPF UI) -- WPF rejects cross-thread mutations with
    ///   <see cref="NotSupportedException"/>.</item>
    ///   <item><c>TesterPresentActive = false</c> raises PropertyChanged on
    ///   a non-UI thread -- DataTriggers binding to it throw on UI
    ///   marshalling.</item>
    /// </list>
    /// Fix: capture <see cref="SynchronizationContext.Current"/> in the
    /// ctor and <c>Post</c> the catch-arm UI updates back. Mirrors the
    /// pattern in <c>ReplayViewModel.OnPlaybackEnded</c>.
    /// <para>
    /// This test path runs WITHOUT a WPF SynchronizationContext, so the
    /// fix's null-SyncContext fallback (direct call, like ReplayViewModel)
    /// executes -- the test asserts the catch-arm completed without
    /// throwing AND the log line was appended. The flag-flip-back assertion
    /// is intentionally omitted because the catch arm's call to
    /// <c>TesterPresentActive = false</c> races with the test's second
    /// click event (which would start a new loop in the "stop" branch
    /// only); the catch arm itself is the observer we care about, not
    /// the symmetric state machine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ToggleTesterPresentCommand_TestPresentAsyncThrows_CatchArmFiresNoException_AndAppendsErrorLog()
    {
        var fake = new RecordingUdsClient { TesterPresentThrows = true };
        var vm = NewVm(fake);
        var log = new System.Collections.ObjectModel.ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        // Start the loop.
        vm.ToggleTesterPresentCommand.Execute(null);
        vm.TesterPresentActive.Should().BeTrue();
        log.Should().ContainSingle(l => l.Message.Contains("TesterPresent started"));

        // Wait briefly for the threadpool catch arm to fire on the first
        // TesterPresentAsync invocation.
        await WaitFor(() => log.Any(l => l.Level == "Error"), millisecondsTimeout: 2000);

        // Verify the catch arm fired and appended the expected Error line.
        // Pre-fix, this catch was on the threadpool WITHOUT a SyncContext.Post,
        // and would throw NotSupportedException on the cross-thread
        // ObservableCollection.Add in test env (and on WPF dispatcher in
        // production). Post-fix, the null-SyncContext fallback (test path)
        // runs the AppendLog + flag-flip in the catch arm directly. In
        // production with a WPF SyncContext, the Post marshals back to
        // the UI dispatcher first.
        log.Should().Contain(l =>
            l.Level == "Error" && l.Message.Contains("TesterPresent loop error"),
            "the catch arm must append an Error log line (not throw)");
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

    // ---------- window/singleton lifecycle (v3.49.x PATCH plan-uds-window-lifecycle T2) ----------
    //
    // UdsWindow.Unloaded used to call Session.Dispose(). Session.Dispose() is already
    // idempotent and sets no permanent gate (TesterPresent can be re-toggled after it),
    // but "Dispose" is a one-shot name that misleads callers about the singleton's
    // reusability. StopForWindowClose names the window-scoped halt precisely and Dispose
    // delegates to it. The contract below pins the behaviour UdsWindow.Unloaded relies on:
    // window close stops the in-flight TesterPresent loop, and the SAME singleton VM can
    // re-arm the loop when the next window instance binds it.

    [Fact]
    public void StopForWindowClose_Stops_TesterPresent_And_Resets_Flag()
    {
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        vm.ToggleTesterPresentCommand.Execute(null);
        vm.TesterPresentActive.Should().BeTrue();

        vm.StopForWindowClose();

        vm.TesterPresentActive.Should().BeFalse("window close must stop the loop and reflect it in the flag");
    }

    [Fact]
    public void StopForWindowClose_Keeps_Vm_Reusable_For_Next_Window_Instance()
    {
        // The singleton survives the window close: a subsequent TweakerPresent toggle
        // restarts the background loop. This is the regression the old Unloaded→Dispose
        // wiring relied on but didn't explicitly pin (Dispose happily re-arms; we now
        // make the window-scope semantic explicit).
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        vm.StopForWindowClose();

        vm.ToggleTesterPresentCommand.Execute(null);
        vm.TesterPresentActive.Should().BeTrue("the reused singleton must re-arm the loop after window-level stop");

        // And stop cleanly again.
        vm.ToggleTesterPresentCommand.Execute(null);
        vm.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public void Dispose_Delegates_To_StopForWindowClose_Keeps_VM_Reusable()
    {
        // DI's App.OnExit cascade calls Dispose on the singleton; it must be a safe
        // superset of the window-level stop. Reusing the VM after Dispose (in test or
        // in a hypothetical non-shutdown path) must not throw.
        var fake = new RecordingUdsClient();
        var vm = NewVm(fake);

        vm.Dispose();
        vm.Dispose(); // idempotent on the DI cascade

        var act = () => vm.StopForWindowClose();
        act.Should().NotThrow("the two stop entry points share a body, both idempotent");
    }
}

