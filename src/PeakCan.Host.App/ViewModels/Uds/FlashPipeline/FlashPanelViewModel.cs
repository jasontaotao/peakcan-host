using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private CancellationTokenSource? _runCts;
    private bool _disposed;

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
        ILogger<FlashPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(stackFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _stackFactory = stackFactory;
        _logger = logger;
    }

    public ObservableCollection<UdsLogLine>? Log { get; private set; }

    public void AttachLog(ObservableCollection<UdsLogLine> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Log = log;
    }

    /// <summary>Start a flash run: build + attach the secondary stack, drive the executor.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsFlashing) return; // defensive: never build a second stack (H1).
        ObjectDisposedException.ThrowIf(_disposed, this);

        var enabled = CurrentProfile.Steps.Where(s => s.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            _logger.LogWarning("Start requested with no enabled steps.");
            Status = FlashStatus.Failed;
            StatusMessage = "No enabled steps.";
            return;
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
            return;
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
            return;
        }

        var snapshots = enabled.Select(ToSnapshot).ToList();

        // Build the stack FIRST and attach — the run owns it for its whole lifetime,
        // so any later pre-flight failure (e.g. missing firmware) still routes through
        // the same finally.teardown. This keeps the teardown order invariant uniform.
        ISecondaryFlashStack? stack = secStep is not null ? _stackFactory.Build(ToSnapshot(secStep), CurrentProfile) : null;
        stack?.AttachToRouter();

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        IsFlashing = true;
        Status = FlashStatus.Running;
        StatusMessage = "Flashing…";
        TotalSteps = snapshots.Count;
        CurrentStepIndex = 0;
        NotifyCommandCanExecute();

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
        }
        catch (OperationCanceledException)
        {
            Status = FlashStatus.Cancelled;
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flash run failed.");
            Status = FlashStatus.Failed;
            StatusMessage = ex.Message;
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

    private bool CanStart() => !IsFlashing && !_disposed;

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

    private bool CanStop() => IsFlashing && !_disposed;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _runCts?.Cancel(); } catch { }
        _runCts?.Dispose();
    }
}
