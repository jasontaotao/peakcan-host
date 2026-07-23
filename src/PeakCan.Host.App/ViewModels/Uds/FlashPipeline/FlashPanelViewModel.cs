using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Panel VM for the Flashing tab: owns the secondary flash-stack lifecycle and the
/// UI-facing IsFlashing / Status / Progress state. Builds the stack via
/// <see cref="ISecondaryFlashStackFactory"/> (a test seam — VM never constructs a UdsClient
/// directly), drives it through <see cref="PipelineExecutor"/>, and tears it down in the
/// strict order Detach→Client.Dispose→IsoTp.Dispose→DllKey.Dispose (enforced by the stack
/// itself).
/// <para>
/// <b>Concurrency arbitration (H1):</b> <see cref="IsFlashing"/> + <see cref="StartCommand"/>
/// <c>CanExecute</c> gate make a second Start while one is running a no-op. Other panels
/// (Session/Did/Routine/Dtc) consume <see cref="IsFlashing"/> and refuse to issue commands
/// while a flash is in flight; at minimum the Session-panel TesterPresent loop is paused
/// by SessionPanelViewModel reading this flag (Phase 1).
/// </para>
/// </summary>
public sealed partial class FlashPanelViewModel : ObservableObject, IUdsPanel, IDisposable
{
    /// <summary>
    /// The diagnostic ISO-TP response CAN-ID (0x7E8) — the singleton diagnostic IsoTpLayer's
    /// ResponseId (AppHostBuilder line 186). The same-addressing degradation check (Task 3.2)
    /// compares the profile's programming ResponseId against this: a programming layer sharing
    /// it would collide with the diagnostic layer's receive path on the shared router.
    /// </summary>
    private const uint DiagnosticResponseId = 0x7E8;

    private readonly ISecondaryFlashStackFactory _stackFactory;
    private readonly ILogger<FlashPanelViewModel> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _linkedLifetimeCts;

    /// <summary>
    /// The currently in-flight flash run, or null when idle. App.OnExit reads this to
    /// await an in-flight flash's <c>finally</c> (which releases the native OEM-DLL handle
    /// via <c>DllKey.Dispose</c>) BEFORE calling <c>_host.Dispose()</c> — without this, a
    /// close + immediate-exit races the finally and the OS reclaims the handle ungracefully
    /// (reviewer MEDIUM-1). null once the run completes so the await is a no-op when idle.
    /// </summary>
    public Task? CurrentRunTask { get; private set; }
    // v3.49.x PATCH (plan-uds-window-lifecycle T1): the one-shot _disposed flag is
    // GONE. FlashPanelViewModel is a DI singleton (AppHostBuilder.cs:284); coupling a
    // permanent "disposed" gate to UdsWindow.Unloaded's Dispose() call made the panel
    // permanently unreachable after the first window close (ObjectDisposedException at
    // StartAsync line below + a perpetually-greyed Start button via CanStart). Dispose
    // now only stops an in-flight run and is fully idempotent/reversible — a re-opened
    // window binds the same singleton and Start works again.

    [ObservableProperty] private FlashProfile _currentProfile = FlashProfile.CreateDefault();
    [ObservableProperty] private bool _isFlashing;
    [ObservableProperty] private FlashStatus _status = FlashStatus.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private int _currentStepIndex;
    [ObservableProperty] private int _totalSteps;

    /// <summary>
    /// internal ctor: <see cref="ISecondaryFlashStackFactory"/> / <see cref="ISecondaryFlashStack"/>
    /// are App-internal seam contracts (visible to tests via InternalsVisibleTo), and a public
    /// ctor taking internal params would violate CS0051 (accessibility-consistency).
    /// </summary>
    internal FlashPanelViewModel(
        ISecondaryFlashStackFactory stackFactory,
        ILogger<FlashPanelViewModel> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(stackFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _stackFactory = stackFactory;
        _logger = logger;
        // MEDIUM-1: lifetime is defaulted for back-compat with pre-existing tests that
        // don't exercise the ApplicationStopping path. Production DI always supplies a
        // real IHostApplicationLifetime; the NullLifetime stand-in is inert (tokens never
        // fire, StopApplication is a no-op) so the linked-token path is never triggered.
        _lifetime = lifetime ?? NullLifetime.Instance;
    }

    public ObservableCollection<UdsLogLine>? Log { get; private set; }

    public void AttachLog(ObservableCollection<UdsLogLine> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Log = log;
    }

    /// <summary>Start a flash run: build + attach the secondary stack, drive the executor.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync()
    {
        if (IsFlashing) return Task.CompletedTask; // defensive: never build a second stack (H1).

        var enabled = CurrentProfile.Steps.Where(s => s.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            _logger.LogWarning("Start requested with no enabled steps.");
            Status = FlashStatus.Failed;
            StatusMessage = "No enabled steps.";
            return Task.CompletedTask;
        }

        var secStep = enabled.FirstOrDefault(s => s.Kind == FlashStepKind.SecurityAccess);
        // Auto mode is unimplemented in Phase 1 → refuse BEFORE touching the stack,
        // so no wire/native work escapes and IsFlashing never lies.
        if (secStep?.SecurityMode == SecurityAccessMode.Auto)
        {
            // C4 review #2: Auto is a configuration choice, so refusing it at run time reports
            // to the operator via Status/StatusMessage (mirroring the same-addressing Dll
            // refusal below), NOT a throw into the [RelayCommand] unobserved-exception path
            // that masks the status text behind a WPF crash dialog. The second-line defence
            // throw remains in SecondaryFlashStackFactory.Build for any Auto snapshot that
            // ever bypasses this VM gate.
            Status = FlashStatus.Failed;
            StatusMessage = "Auto SecurityAccess mode is not supported in Phase 1.";
            return Task.CompletedTask;
        }

        // Task 3.2 同寻址退化: if the programming CAN-ID pair degrades to the diagnostic
        // pair (ResponseId == 0x7E8), the secondary IsoTpLayer collides with the diagnostic
        // one on the shared router (ReceiveFlow filters by ResponseId — two layers with the
        // SAME ResponseId both grab every ECU response, corrupting both). This is a real
        // collision, not a stylistic one. Dll mode in the degraded case still works on the
        // wire, but the operator intent was almost certainly a misconfigured profile (the
        // de-facto programming pair 0x714/0x760 is distinct by default); refuse Start with a
        // self-explaining message rather than silently corrupting the diagnostic session.
        if (secStep is { SecurityMode: SecurityAccessMode.Dll }
            && CurrentProfile.ProgrammingCanId.ResponseId == DiagnosticResponseId)
        {
            Status = FlashStatus.Failed;
            StatusMessage =
                "编程寻址与诊断寻址相同 (0x7E8) — 同寻址刷写仅支持 Manual mode。请将 ProgrammingCanId 改为不同于 0x7E0/0x7E8 的编程寻址。";
            _logger.LogWarning("Refused Dll-mode flash: programming ResponseId collides with diagnostic 0x7E8.");
            return Task.CompletedTask;
        }

        var snapshots = enabled.Select(ToSnapshot).ToList();

        // Build the stack FIRST and attach — the run owns it for its whole lifetime,
        // so any later pre-flight failure (e.g. missing firmware) still routes through
        // the same finally.teardown. This keeps the teardown order invariant uniform.
        ISecondaryFlashStack? stack = secStep is not null ? _stackFactory.Build(ToSnapshot(secStep), CurrentProfile) : null;
        stack?.AttachToRouter();

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        // MEDIUM-1: link the run's CT to ApplicationStopping so App.OnExit's host.StopAsync
        // cascade cancels an in-flight flash (not just StopForWindowClose). The linked CTS
        // ties the two without the run seeing StopForWindowClose's CT as the trigger.
        _linkedLifetimeCts?.Dispose();
        _linkedLifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(
            _runCts.Token, _lifetime.ApplicationStopping);
        var ct = _linkedLifetimeCts.Token;

        IsFlashing = true;
        Status = FlashStatus.Running;
        StatusMessage = "Flashing…";
        TotalSteps = snapshots.Count;
        CurrentStepIndex = 0;
        NotifyCommandCanExecute();

        // MEDIUM-1: capture the in-flight run so App.OnExit can await the finally (which
        // releases the native OEM-DLL handle) BEFORE _host.Dispose(). We can't reference
        // StartAsync's own Task from inside its own body, so we wrap the real work in a
        // TaskCompletionSource, assign its task to CurrentRunTask SYNCHRONOUSLY (before
        // StartAsync yields), and return the inner async method's Task directly so the
        // caller's await observes the TRUE terminal state (including the finally). The TCS
        // task and the inner task settle in lockstep — when the inner finally runs, it clears
        // CurrentRunTask AND we settle the TCS, so both the captured reference and the caller
        // see the same completion.
        var tcs = new TaskCompletionSource<object?>();
        CurrentRunTask = tcs.Task;
        return RunFlashOnceAsync(tcs, enabled, snapshots, stack, ct);
    }

    /// <summary>
    /// The actual flash run body, extracted so <see cref="StartAsync"/> can capture the
    /// in-flight task for <see cref="CurrentRunTask"/> (MEDIUM-1). Runs the executor +
    /// finally teardown and settles <paramref name="tcs"/> so the captured task reflects the
    /// true terminal state (including the finally). Exceptions are caught here and translated
    /// to the UI-facing Status/StatusMessage.
    /// </summary>
    private async Task RunFlashOnceAsync(
        TaskCompletionSource<object?> tcs,
        List<FlashStep> enabled,
        List<FlashStepSnapshot> snapshots,
        ISecondaryFlashStack? stack,
        CancellationToken ct)
    {
        try
        {
            // Resolve firmware BEFORE the executor runs — a missing/garbage file fails the run
            // fast with a clean Failed status (no half-flash). The stack is already attached
            // and will be torn down by the finally below.
            FirmwareImage? firmware = null;
            var dlStep = enabled.FirstOrDefault(s => s.Kind == FlashStepKind.DownloadTransfer);
            if (dlStep is not null)
            {
                firmware = await LoadFirmwareOrThrowAsync(dlStep).ConfigureAwait(false);
            }

            var driveClient = stack?.Client ??
                throw new InvalidOperationException("Secondary stack was not built (no SecurityAccess step).");
            var progress = new Progress<FlashProgress>(OnProgress);
            await PipelineExecutor.ExecuteAsync(
                driveClient, snapshots, firmware, progress, ct).ConfigureAwait(false);
            // PipelineExecutor reports per-step; the terminal Success is signalled by absence of throw.
            Status = FlashStatus.Success;
            StatusMessage = "Flash complete.";
            tcs.SetResult(null);
        }
        catch (OperationCanceledException)
        {
            Status = FlashStatus.Cancelled;
            StatusMessage = "Cancelled.";
            tcs.SetResult(null); // cancellation is a terminal state, not a fault
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flash run failed.");
            Status = FlashStatus.Failed;
            StatusMessage = ex.Message;
            tcs.SetException(ex);
        }
        finally
        {
            // Strict teardown order: detach the receive adapter BEFORE releasing the
            // client/isoTp/DllKey, so no late router frame is delivered to a disposing
            // IsoTpLayer (which would fault the SDK read thread).
            stack?.DetachFromRouter();
            stack?.Dispose();
            IsFlashing = false;
            NotifyCommandCanExecute();
            // MEDIUM-1: clear the in-flight task now that the run (and its finally) has
            // completed — App.OnExit's await has observed the terminal state.
            CurrentRunTask = null;
        }
    }

    private async Task<FirmwareImage> LoadFirmwareOrThrowAsync(FlashStep dlStep)
    {
        if (string.IsNullOrWhiteSpace(dlStep.FirmwarePath))
        {
            throw new InvalidOperationException("DownloadTransfer step has no firmware path.");
        }
        var bytes = await File.ReadAllBytesAsync(dlStep.FirmwarePath).ConfigureAwait(false);
        return FirmwareFileParser.Parse(bytes);
    }

    private bool CanStart() => !IsFlashing;

    /// <summary>Cancel the in-flight flash run. No-op if idle. Idempotent — safe to call after completion.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StopAsync swallowed an exception cancelling the run.");
        }
        return Task.CompletedTask;
    }

    private bool CanStop() => IsFlashing;

    private void NotifyCommandCanExecute()
    {
        OnPropertyChanged(nameof(StartCommand));
        OnPropertyChanged(nameof(StopCommand));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnProgress(FlashProgress p)
    {
        Status = p.Status;
        CurrentStepIndex = p.CurrentStepIndex;
        TotalSteps = p.TotalSteps;
        StatusMessage = p.Message ?? StatusMessage;
        if (p.CurrentStepTotalBytes is { } total && total > 0 && p.CurrentStepDoneBytes is { } done)
        {
            ProgressPercent = (int)(done * 100 / total);
        }
    }

    private static FlashStepSnapshot ToSnapshot(FlashStep step) => new()
    {
        Kind = step.Kind,
        IsEnabled = step.IsEnabled,
        SecurityLevel = step.SecurityLevel,
        SecurityMode = step.SecurityMode,
        ManualKeyHex = step.ManualKeyHex,
        DllPath = step.DllPath,
        RoutineId = step.RoutineId,
        MemoryAddress = step.MemoryAddress,
        ResetType = step.ResetType,
        AutoResetOnFailure = step.AutoResetOnFailure,
    };

    /// <summary>
    /// Window-level halt (v3.49.x PATCH plan T1): called by <c>UdsWindow.Unloaded</c>
    /// when the UDS diagnostic window closes. Stops any in-flight run by cancelling its
    /// <see cref="CancellationTokenSource"/>; the in-flight <see cref="StartAsync"/>
    /// catch arm then routes to <see cref="FlashStatus.Cancelled"/> and its <c>finally</c>
    /// tears the secondary stack down in the strict Detach→Client→IsoTp→DllKey order.
    /// <para>
    /// Idempotent and <b>non-terminating</b>: unlike a traditional <see cref="IDisposable"/>,
    /// this does NOT put the VM in a one-shot "disposed" state. <see cref="FlashPanelViewModel"/>
    /// is a DI singleton (<c>AppHostBuilder.cs:284</c>) shared across window open/close
    /// cycles, so a close must leave it reusable for the next opened window. The removed
    /// <c>_disposed</c> gate permanently froze the panel after the first close; this method
    /// restores the per-window, per-run scoping the single instance actually needs.
    /// </para><para>
    /// Process shutdown still gets a real teardown via <see cref="Dispose"/> (DI cascade
    /// from <c>App.OnExit</c>'s <c>_host.Dispose()</c> at <c>App.xaml.cs:190</c>), which
    /// routes here — native OEM DLL handles (DllKeyDerivationAlgorithm's NativeLibrary.Load
    /// output) are released by the stack's own <c>finally</c>, not by this method.
    /// </para>
    /// </summary>
    public void StopForWindowClose()
    {
        try { _runCts?.Cancel(); } catch { }
        _runCts?.Dispose();
        _runCts = null;
        // The linked CTS is tied to _runCts.Token — once the run CT is gone the link is
        // inert, but we still release it for determinism. Harmless if ApplicationStopping
        // already fired (the linked CT self-disposes only via us, not via token cancellation).
        _linkedLifetimeCts?.Dispose();
        _linkedLifetimeCts = null;
    }

    public void Dispose() => StopForWindowClose();

    /// <summary>
    /// Inert <see cref="IHostApplicationLifetime"/> for callers that don't supply one (back-compat
    /// tests, non-DI construction). All three tokens are pre-cancelled... no, pre-CANCELLED tokens
    /// would fire the linked path. Instead we use NEVER-cancelled tokens so the linked CTS in
    /// <see cref="StartAsync"/> never sees ApplicationStopping fire and the run behaves exactly
    /// like the pre-MEDIUM-1 design (only StopForWindowClose can cancel it). Singleton — stateless.
    /// </summary>
    private sealed class NullLifetime : IHostApplicationLifetime
    {
        public static NullLifetime Instance { get; } = new();
        private NullLifetime() { }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
