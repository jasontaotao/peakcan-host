using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using IOPath = System.IO.Path;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.FlashPipeline;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

/// <summary>
/// Phase 2 扁平化 Segment 的绑定友好包装: 带所属固件文件名, 供 ComboBox 下拉显示.
/// </summary>
public sealed record SegmentDisplayItem(string FileName, Segment Segment)
{
    public uint StartAddress => Segment.StartAddress;
    public uint EndAddress => Segment.EndAddress;
    public uint Length => Segment.Length;
    public byte[] Data => Segment.Data;
    public uint Crc32 => Segment.Crc32;
}

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
    private readonly IFileDialogService _fileDialog;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly FlashConfigurationService? _flashConfig;

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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectDllCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectFirmwareCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadProfileCommand))]
    private bool _isFlashing;
    [ObservableProperty] private FlashStatus _status = FlashStatus.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private int _currentStepIndex;
    [ObservableProperty] private int _totalSteps;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectDllCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectFirmwareCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private FlashStep? _selectedStep;

    [ObservableProperty]
    private FirmwareFile? _selectedFirmwareFile;

    /// <summary>
    /// Issue 1: 当前选中 Erase 步骤的 Segment 索引 (VM 级, 用于 ComboBox 绑定).
    /// </summary>
    [ObservableProperty]
    private int _eraseSegmentIndex = -1;

    /// <summary>
    /// Issue 2: 订阅当前 profile 的 FirmwareFiles.CollectionChanged, 以便在 firmware 文件
    /// 增删时刷新 AllSegments 绑定.
    /// </summary>
    private ObservableCollection<FirmwareFile>? _subscribedFirmwareFiles;

    /// <summary>
    /// internal ctor: <see cref="ISecondaryFlashStackFactory"/> / <see cref="ISecondaryFlashStack"/>
    /// are App-internal seam contracts (visible to tests via InternalsVisibleTo), and a public
    /// ctor taking internal params would violate CS0051 (accessibility-consistency).
    /// </summary>
    internal FlashPanelViewModel(
        ISecondaryFlashStackFactory stackFactory,
        ILogger<FlashPanelViewModel> logger,
        IFileDialogService? fileDialog = null,
        IHostApplicationLifetime? lifetime = null,
        FlashConfigurationService? flashConfig = null)
    {
        ArgumentNullException.ThrowIfNull(stackFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _stackFactory = stackFactory;
        _logger = logger;
        // Phase 1.1: fileDialog is defaulted for back-compat with pre-existing tests that
        // don't exercise file browsing. Production DI always supplies a real IFileDialogService;
        // NullFileDialogService returns null (user cancelled) so commands silently no-op.
        _fileDialog = fileDialog ?? NullFileDialogService.Instance;
        // MEDIUM-1: lifetime is defaulted for back-compat with pre-existing tests that
        // don't exercise the ApplicationStopping path. Production DI always supplies a
        // real IHostApplicationLifetime; the NullLifetime stand-in is inert (tokens never
        // fire, StopApplication is a no-op) so the linked-token path is never triggered.
        _lifetime = lifetime ?? NullLifetime.Instance;
        // Phase 2 (spec §8): ODX-derived flash configuration. Subscribe to
        // ConfigUpdated so ODX import auto-fills SecurityAccess defaults.
        _flashConfig = flashConfig;
        if (_flashConfig is not null)
            _flashConfig.ConfigUpdated += ApplyOdxDefaultsIfUnset;
        // Issue 2: 订阅初始 profile 的 FirmwareFiles 集合变更.
        SubscribeFirmwareFiles();
        // Issue: Verify 步骤的 Segment 索引变更时自动填充地址+CRC.
        VerifyParams.SegmentResolver = SegmentAtIndex;
    }

    /// <summary>
    /// Issue 2: 订阅当前 profile 的 FirmwareFiles.CollectionChanged, 以便在 firmware 文件
    /// 增删时刷新 AllSegments 绑定 (Download/Verify/Erase 的 Segment ComboBox).
    /// profile 切换时取消旧订阅并重新订阅新集合.
    /// </summary>
    private void SubscribeFirmwareFiles()
    {
        if (_subscribedFirmwareFiles is not null)
            _subscribedFirmwareFiles.CollectionChanged -= OnFirmwareFilesChanged;
        _subscribedFirmwareFiles = null;

        if (CurrentProfile.FirmwareFiles is ObservableCollection<FirmwareFile> fwf)
        {
            fwf.CollectionChanged += OnFirmwareFilesChanged;
            _subscribedFirmwareFiles = fwf;
        }
        RefreshAllSegments();
    }

    private void OnFirmwareFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshAllSegments();

    /// <summary>
    /// Phase 2 (spec §8): apply ODX-derived SecurityAccess defaults to steps that
    /// are still at their factory defaults. Called on ODX import (via ConfigUpdated
    /// event) and after profile load. Only fills unset values — never overrides
    /// operator-edited or profile-saved config (C2).
    /// </summary>
    internal void ApplyOdxDefaultsIfUnset()
    {
        var config = _flashConfig?.GetSecurityAccessConfig();
        if (config is null) return;

        foreach (var step in CurrentProfile.Steps.Where(s => s.Kind == FlashStepKind.SecurityAccess))
        {
            // Only fill Level if still at factory default (0x01).
            if (step.SecurityAccess?.Level == 0x01)
            {
                step.SetSecurityAccessLevel(config.Level);
                step.SetOdxDerivedBaseline(config.Level, config.SeedLength);
            }
            // Only fill SeedLength if still at factory default (null = auto).
            if (step.SecurityAccess?.SeedLength == null)
            {
                step.SetSeedLength(config.SeedLength);
                step.SetOdxDerivedBaseline(config.Level, config.SeedLength);
            }
        }
    }

    /// <summary>Issue 2: 刷新缓存的 AllSegments 列表并通知绑定.</summary>
    private void RefreshAllSegments()
    {
        _allSegments = CurrentProfile.FirmwareFiles
            .SelectMany(f => f.Segments.Select(s => new SegmentDisplayItem(f.Path, s)))
            .ToList();
        OnPropertyChanged(nameof(AllSegments));
    }

    /// <summary>
    /// Issue 2: profile 切换时重新订阅 FirmwareFiles 集合并刷新 AllSegments.
    /// </summary>
    partial void OnCurrentProfileChanged(FlashProfile value) => SubscribeFirmwareFiles();

    /// <summary>
    /// Issue 1: 当 operator 选择/更改 Erase 步骤的 Segment 索引时, 自动解析 Segment 地址
    /// 并填充到 RoutineControl 的 StartAddress/Size.
    /// </summary>
    partial void OnEraseSegmentIndexChanged(int value)
    {
        if (SelectedStep is not { Kind: FlashStepKind.Erase } step || step.RoutineControl is null) return;
        step.RoutineControl.SegmentIndex = value;
        if (SegmentAtIndex(value) is { } seg)
            step.RoutineControl.ApplySegmentAddress(seg.StartAddress, seg.Length);
        else
            step.RoutineControl.ApplySegmentAddress(0, 0);
    }

    /// <summary>
    /// Issue 1: 当选中的步骤切换时, 同步 EraseSegmentIndex 到当前 Erase 步骤的 SegmentIndex.
    /// </summary>
    partial void OnSelectedStepChanged(FlashStep? value)
    {
        // Bug fix: 直接写 _eraseSegmentIndex 不触发 [ObservableProperty] 生成的 setter,
        // UI 收不到 EraseSegmentIndex 的 PropertyChanged 通知, ComboBox 显示空选择.
        // 用属性赋值触发 setter -> OnEraseSegmentIndexChanged -> 同步地址. 循环安全:
        // OnEraseSegmentIndexChanged 写回 step.RoutineControl.SegmentIndex, 但 RoutineControlParams
        // 的 SegmentIndex setter 有相等检查 (if (_segmentIndex == value) return), 值相同时 no-op.
        if (value is { Kind: FlashStepKind.Erase, RoutineControl: { } rc })
            EraseSegmentIndex = rc.SegmentIndex;
        else
            EraseSegmentIndex = -1;
    }

    /// <summary>
    /// SecurityAccessMode values for the property panel ComboBox.
    /// </summary>
    public IReadOnlyList<SecurityAccessMode> SecurityAccessModes { get; } =
        Enum.GetValues<SecurityAccessMode>();

    /// <summary>
    /// EcuResetType values for the property panel ComboBox.
    /// </summary>
    public IReadOnlyList<EcuResetType> EcuResetTypes { get; } =
        Enum.GetValues<EcuResetType>();

    /// <summary>
    /// ChecksumAlgorithm values for the Verify step ComboBox.
    /// </summary>
    public IReadOnlyList<ChecksumAlgorithm> ChecksumAlgorithms { get; } =
        Enum.GetValues<ChecksumAlgorithm>();

    /// <summary>
    /// Issue 3: CRC 算法预设名称列表 (用于 Verify 步骤 ComboBox).
    /// 前 4 项对应 CrcParameters.Presets, 最后一项 "Custom".
    /// </summary>
    public IReadOnlyList<string> CrcPresetNames { get; } =
        [.. PeakCan.HIL.Core.Uds.FlashPipeline.CrcParameters.PresetNames, "Custom"];

    /// <summary>
    /// AddressingMode values for the per-step ComboBox.
    /// </summary>
    public IReadOnlyList<PeakCan.HIL.Core.Uds.FlashPipeline.AddressingMode> AddressingModes { get; } =
        Enum.GetValues<PeakCan.HIL.Core.Uds.FlashPipeline.AddressingMode>();

    /// <summary>
    /// CommunicationSubFunction values for the 0x28 step ComboBox.
    /// </summary>
    public IReadOnlyList<PeakCan.HIL.Core.Uds.CommunicationSubFunction> CommunicationSubFunctions { get; } =
        Enum.GetValues<PeakCan.HIL.Core.Uds.CommunicationSubFunction>();

    /// <summary>
    /// DtcControlSubFunction values for the 0x14 step ComboBox.
    /// </summary>
    public IReadOnlyList<PeakCan.HIL.Core.Uds.DtcControlSubFunction> DtcControlSubFunctions { get; } =
        Enum.GetValues<PeakCan.HIL.Core.Uds.DtcControlSubFunction>();

    /// <summary>
    /// Kinds the operator can add via the "Add Step" dropdown. Excludes
    /// <see cref="FlashStepKind.SessionControl"/> (no configurable parameters, always runs).
    /// </summary>
    public static IReadOnlyList<FlashStepKind> AddableKinds { get; } =
    [
        FlashStepKind.PreCheck,
        FlashStepKind.SecurityAccess,
        FlashStepKind.Erase,
        FlashStepKind.DownloadTransfer,
        FlashStepKind.Verify,
        FlashStepKind.EcuReset,
        FlashStepKind.CommunicationControl,
        FlashStepKind.DtcControl,
        FlashStepKind.DependencyCheck,
        FlashStepKind.FlashDriverDownload,
    ];

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
    /// <para>
    /// <b>Thread safety:</b> The PipelineExecutor uses <c>ConfigureAwait(false)</c> internally,
    /// so the outer catch/finally blocks run on a thread-pool thread as well.
    /// <c>[ObservableProperty]</c> setters fire PropertyChanged, which WPF binding handles
    /// on the same thread — that would crash with InvalidOperationException (cross-thread UI
    /// access). We marshal all property writes back to the captured UI SynchronizationContext
    /// via <c>_uiContext.Post</c>. The teardown calls (DetachFromRouter/Dispose) are not
    /// UI-bound and run inline on the thread-pool thread.
    /// </para>
    /// </summary>
    private async Task RunFlashOnceAsync(
        TaskCompletionSource<object?> tcs,
        List<FlashStep> enabled,
        List<FlashStepSnapshot> snapshots,
        ISecondaryFlashStack? stack,
        CancellationToken ct)
    {
        // Capture the UI SynchronizationContext BEFORE the first ConfigureAwait(false) —
        // the method's catch/finally blocks need to marshal [ObservableProperty] writes
        // back to the UI thread to avoid WPF cross-thread InvalidOperationException.
        // When null (e.g. test environment with no WPF Dispatcher), PostOrInline falls
        // back to direct synchronous execution — safe for tests because there's no UI
        // binding thread to protect.
        var uiContext = SynchronizationContext.Current;
        try
        {
            // Phase 1.1: resolve firmware per-step BEFORE the executor runs — a missing/garbage
            // file fails the run fast with a clean Failed status (no half-flash). The stack is
            // already attached and will be torn down by the finally below. Multiple
            // DownloadTransfer steps (flash_driver→RAM then main app, dual-file) each get their
            // own firmware image, indexed by position in the enabled-steps list.
            var firmwareByIndex = await LoadAllFirmwareAsync(enabled).ConfigureAwait(false);

            // Phase 2: Build a flattened segment list for address resolution.
            var allSegments = CurrentProfile.FirmwareFiles
                .SelectMany(f => f.Segments).ToList();
            PipelineExecutor.SegmentAddressResolver? segResolver = idx =>
                (idx >= 0 && idx < allSegments.Count) ? allSegments[idx].StartAddress : null;

            var driveClient = stack?.Client ??
                throw new InvalidOperationException("Secondary stack was not built (no SecurityAccess step).");
            var progress = new Progress<FlashProgress>(OnProgress);
            await PipelineExecutor.ExecuteAsync(
                driveClient, snapshots,
                (step, index) => firmwareByIndex.TryGetValue(index, out var fw) ? fw : null,
                segResolver,
                CurrentProfile.FlashDriver,
                CurrentProfile.AutoResetOnFailure,
                progress, ct).ConfigureAwait(false);
            // PipelineExecutor reports per-step; the terminal Success is signalled by absence of throw.
            PostOrExecute(uiContext, () =>
            {
                Status = FlashStatus.Success;
                StatusMessage = "Flash complete.";
            });
            tcs.SetResult(null);
        }
        catch (OperationCanceledException)
        {
            PostOrExecute(uiContext, () =>
            {
                Status = FlashStatus.Cancelled;
                StatusMessage = "Cancelled.";
            });
            tcs.SetResult(null); // cancellation is a terminal state, not a fault
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flash run failed.");
            var msg = ex.Message;
            PostOrExecute(uiContext, () =>
            {
                Status = FlashStatus.Failed;
                StatusMessage = msg;
            });
            tcs.SetException(ex);
        }
        finally
        {
            // Strict teardown order: detach the receive adapter BEFORE releasing the
            // client/isoTp/DllKey, so no late router frame is delivered to a disposing
            // IsoTpLayer (which would fault the SDK read thread). These are infrastructure
            // calls — thread-safe and not UI-bound.
            stack?.DetachFromRouter();
            stack?.Dispose();
            // UI-bound state writes must go through the captured SynchronizationContext.
            PostOrExecute(uiContext, () =>
            {
                IsFlashing = false;
                NotifyCommandCanExecute();
                // MEDIUM-1: clear the in-flight task now that the run (and its finally) has
                // completed — App.OnExit's await has observed the terminal state.
                CurrentRunTask = null;
            });
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

    /// <summary>
    /// Pre-load firmware for every enabled DownloadTransfer step, keyed by its index in the
    /// enabled-steps list. Same file path is read once (dedup via <paramref name="seenPaths"/>).
    /// The executor's per-step resolver uses the index to return the correct image — this is
    /// robust to two DownloadTransfer steps with identical parameters (which would collide as
    /// snapshot-value dictionary keys, since <see cref="FlashStepSnapshot"/> carries no
    /// FirmwarePath). Steps with empty FirmwarePath are skipped; the executor throws
    /// InvalidOperationException when it hits them.
    /// </summary>
    private async Task<Dictionary<int, FirmwareImage>> LoadAllFirmwareAsync(List<FlashStep> enabledSteps)
    {
        var dict = new Dictionary<int, FirmwareImage>();
        // C-3/C-4 fix: Phase 2 path uses FirmwareFiles + SegmentIndex (primary).
        // Phase 1.1 path uses FirmwarePath (backward compat, now uses ParseFile for HEX/S19).
        var firmwareByPath = new Dictionary<string, FirmwareImage>();

        for (int i = 0; i < enabledSteps.Count; i++)
        {
            var step = enabledSteps[i];
            if (step.Kind != FlashStepKind.DownloadTransfer) continue;

            // Phase 2: resolve from FirmwareFiles + SegmentIndex (the primary UI path).
            if (step.Download is { } dl && dl.SegmentIndex >= 0)
            {
                var seg = SegmentAtIndex(dl.SegmentIndex);
                if (seg is null)
                    throw new InvalidOperationException(
                        $"DownloadTransfer step {i} references SegmentIndex {dl.SegmentIndex} which is out of range.");
                // Segment.Data is the parsed payload (from ParseFile at AddFirmwareFile time).
                dict[i] = FirmwareFileParser.Parse(seg.Data);
                continue;
            }

            // Phase 1.1 backward compat: read from FirmwarePath.
            if (string.IsNullOrWhiteSpace(step.FirmwarePath)) continue;
            if (!firmwareByPath.TryGetValue(step.FirmwarePath, out var parsed))
            {
                // C-3 fix: use ParseFile (format-detecting) instead of Parse (raw binary only).
                // This correctly handles .hex/.s19 files referenced via FirmwarePath.
                var file = FirmwareFileParser.ParseFile(step.FirmwarePath);
                // Flatten all segments into a single image for the legacy single-address path.
                var allBytes = file.Segments.SelectMany(s => s.Data).ToArray();
                parsed = FirmwareFileParser.Parse(allBytes);
                firmwareByPath[step.FirmwarePath] = parsed;
            }
            dict[i] = parsed;
        }
        return dict;
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

    // ---- firmware file + flash driver loading (Phase 2) ----

    /// <summary>
    /// Phase 2: Browse for and load a firmware file (.hex / .s19 / .bin). The file is parsed
    /// into segments and added to <see cref="FlashProfile.FirmwareFiles"/>. Download steps
    /// reference segments from this list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSteps))]
    private void AddFirmwareFile()
    {
        var path = _fileDialog.ShowOpenDialog(
            "Firmware files (*.hex;*.s19;*.bin)|*.hex;*.s19;*.bin|Intel HEX (*.hex)|*.hex|Motorola S19 (*.s19)|*.s19|Raw binary (*.bin)|*.bin|All files|*.*");
        if (path is null) return;  // user cancelled
        try
        {
            var file = FirmwareFileParser.ParseFile(path);
            CurrentProfile.FirmwareFiles.Add(file);
            StatusMessage = $"Loaded {file.Format}: {IOPath.GetFileName(path)} ({file.Segments.Count} segment(s))";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse firmware file {Path}", path);
            Status = FlashStatus.Failed;
            StatusMessage = $"Failed to load firmware: {ex.Message}";
        }
    }

    /// <summary>
    /// Phase 2: Browse for and load a flash driver (DLL or binary). The driver is downloaded
    /// to ECU RAM before the main firmware and executed to perform erase/write operations.
    /// 替换语义: 重新加载会覆盖旧 driver (profile 只保留一个 driver 槽位)。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSteps))]
    private void AddFlashDriver()
    {
        var path = _fileDialog.ShowOpenDialog(
            "Flash driver (*.hex;*.s19;*.bin)|*.hex;*.s19;*.bin|Intel HEX (*.hex)|*.hex|Motorola S19 (*.s19)|*.s19|Raw binary (*.bin)|*.bin|All files|*.*");
        if (path is null) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            // Issue 3: 解析 flash driver 文件为 Segments (HEX/S19 多地址段, raw binary 单段).
            var segments = ParseFlashDriverSegments(path);
            CurrentProfile.FlashDriver = new FlashDriver(path, bytes) { Segments = segments };
            OnPropertyChanged(nameof(CurrentProfile));
            OnPropertyChanged(nameof(FlashDriverSegments));
            OnPropertyChanged(nameof(FlashDriverSegmentCount));
            StatusMessage = $"Loaded flash driver: {IOPath.GetFileName(path)} ({bytes.Length} bytes, {segments.Count} segment(s))";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load flash driver {Path}", path);
            Status = FlashStatus.Failed;
            StatusMessage = $"Failed to load driver: {ex.Message}";
        }
    }

    /// <summary>
    /// Phase 2: Remove the loaded flash driver from the profile. No-op if none is loaded.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSteps))]
    private void RemoveFlashDriver()
    {
        CurrentProfile.FlashDriver = null;
        OnPropertyChanged(nameof(CurrentProfile));
        OnPropertyChanged(nameof(FlashDriverSegments));
        OnPropertyChanged(nameof(FlashDriverSegmentCount));
    }

    // ---- step add/remove (Phase 1.1) ----

    /// <summary>
    /// Append a new step of the given <paramref name="kind"/> to the pipeline. The new row
    /// carries kind-appropriate defaults (see <see cref="FlashStep"/>(FlashStepKind)).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddStep))]
    private void AddStep(FlashStepKind kind) => CurrentProfile.Steps.Add(new FlashStep(kind));

    /// <summary>
    /// Remove the currently selected step. No-op if nothing is selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveStep))]
    private void RemoveStep()
    {
        if (SelectedStep is { } step)
            CurrentProfile.Steps.Remove(step);
    }

    /// <summary>
    /// Phase 2: Remove the currently selected firmware file from the profile. No-op if nothing is selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSteps))]
    private void RemoveFirmwareFile()
    {
        if (SelectedFirmwareFile is { } file)
            CurrentProfile.FirmwareFiles.Remove(file);
    }

    /// <summary>Move the selected step up in the pipeline order.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedStep is null) return;
        var idx = CurrentProfile.Steps.IndexOf(SelectedStep);
        if (idx <= 0) return;
        CurrentProfile.Steps.Move(idx, idx - 1);
    }

    /// <summary>Move the selected step down in the pipeline order.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedStep is null) return;
        var idx = CurrentProfile.Steps.IndexOf(SelectedStep);
        if (idx < 0 || idx >= CurrentProfile.Steps.Count - 1) return;
        CurrentProfile.Steps.Move(idx, idx + 1);
    }

    private bool CanAddStep() => !IsFlashing;
    private bool CanRemoveStep() => !IsFlashing && SelectedStep is not null;
    private bool CanMoveUp() => !IsFlashing && SelectedStep is not null && CurrentProfile.Steps.IndexOf(SelectedStep) > 0;
    private bool CanMoveDown() => !IsFlashing && SelectedStep is not null && CurrentProfile.Steps.IndexOf(SelectedStep) < CurrentProfile.Steps.Count - 1;

    // ---- file browse (Phase 1.1) ----

    /// <summary>
    /// Browse for the OEM SecurityAccess DLL. Writes to the selected SecurityAccess step's
    /// <see cref="FlashStep.DllPath"/>. No-op if the selected step is not SecurityAccess
    /// (CanExecute guards this) or the user cancels the dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectDll))]
    private void SelectDll()
    {
        var path = _fileDialog.ShowOpenDialog("DLL files (*.dll)|*.dll|All files|*.*");
        if (path is null) return;
        if (SelectedStep is { Kind: FlashStepKind.SecurityAccess } step)
            step.DllPath = path;
    }

    /// <summary>
    /// Browse for the firmware binary. Writes to the selected DownloadTransfer step's
    /// <see cref="FlashStep.FirmwarePath"/>. No-op if the selected step is not
    /// DownloadTransfer (CanExecute guards this) or the user cancels the dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectFirmware))]
    private void SelectFirmware()
    {
        var path = _fileDialog.ShowOpenDialog("Binary files (*.bin)|*.bin|All files|*.*");
        if (path is null) return;
        if (SelectedStep is { Kind: FlashStepKind.DownloadTransfer } step)
            step.FirmwarePath = path;
    }

    private bool CanSelectDll() => !IsFlashing && SelectedStep is { Kind: FlashStepKind.SecurityAccess };
    private bool CanSelectFirmware() => !IsFlashing && SelectedStep is { Kind: FlashStepKind.DownloadTransfer };

    // ---- profile save/load (Phase 1.1) ----

    /// <summary>
    /// Persist the current <see cref="FlashProfile"/> (steps + ProgrammingCanId + timing) to a
    /// JSON file. <see cref="FlashProfile"/> is a full-state snapshot — LoadProfile restores it
    /// wholesale. Errors (path not found, permission denied) surface via StatusMessage.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSteps))]
    private async Task SaveProfileAsync()
    {
        try
        {
            var path = _fileDialog.ShowSaveDialog(
                "Flash profile (*.flash.json)|*.flash.json|All files|*.*", ".flash.json", null);
            if (path is null) return;
            var json = CurrentProfile.ToJson();
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            StatusMessage = $"Profile saved to {path}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save profile");
            Status = FlashStatus.Failed;
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Load a <see cref="FlashProfile"/> from a JSON file, replacing the current profile.
    /// Invalid JSON or read errors surface via StatusMessage (no throw into the
    /// [RelayCommand] unobserved-exception path).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSteps))]
    private async Task LoadProfileAsync()
    {
        try
        {
            var path = _fileDialog.ShowOpenDialog(
                "Flash profile (*.flash.json)|*.flash.json|All files|*.*");
            if (path is null) return;
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            // Clear SelectedStep BEFORE swapping the profile so OnSelectedStepChanged
            // runs against the OLD step (harmless) rather than letting the ListBox
            // auto-null it AFTER the swap (which would leave EraseSegmentIndex stale
            // and the Erase Segment ComboBox showing the wrong selection).
            SelectedStep = null;
            CurrentProfile = FlashProfile.FromJson(json);
            // RefreshAllSegments runs via OnCurrentProfileChanged; EraseSegmentIndex is
            // now -1 (cleared by OnSelectedStepChanged above) and will re-sync when the
            // operator selects the Erase step again.
            // Phase 2 (spec §8): after profile load, apply ODX defaults to steps still at factory values.
            ApplyOdxDefaultsIfUnset();
            StatusMessage = $"Profile loaded from {path}";
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to load profile — invalid JSON");
            Status = FlashStatus.Failed;
            StatusMessage = $"Load failed: invalid profile file ({ex.Message})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load profile");
            Status = FlashStatus.Failed;
            StatusMessage = $"Load failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Guard for profile persistence operations (Save/Load). True when idle — loading a profile
    /// mid-flash would race the in-flight run's step iteration. Logically equivalent to
    /// CanAddStep but semantically named for the persistence use case.
    /// </summary>
    private bool CanEditSteps() => !IsFlashing;

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

    private FlashStepSnapshot ToSnapshot(FlashStep step) => new()
    {
        Kind = step.Kind,
        IsEnabled = step.IsEnabled,
        AddressingMode = step.AddressingMode,

        // Phase 2: Grouped params — only the matching Kind's group is populated.
        PreCheck = step.PreCheck is { } pc ? new PreCheckSnapshot(pc.RoutineId) : null,
        SecurityAccess = step.SecurityAccess is { } sa ? new SecurityAccessSnapshot(sa.Level, sa.Mode, sa.ManualKeyHex, sa.DllPath, sa.SeedLength) : null,
        // Issue 1: Erase 步骤如果引用了 Segment, 自动用 Segment 的地址+大小覆盖手工填写的 StartAddress/Size.
        RoutineControl = step.RoutineControl is { } rc
            ? new RoutineControlSnapshot(rc.RoutineId,
                step.Kind == FlashStepKind.Erase && SegmentAtIndex(rc.SegmentIndex) is { } seg ? seg.StartAddress : rc.StartAddress,
                step.Kind == FlashStepKind.Erase && SegmentAtIndex(rc.SegmentIndex) is { } seg2 ? seg2.Length : rc.Size)
            : null,
        Download = step.Download is { } dl ? new DownloadSnapshot(dl.SegmentIndex) : null,
        // Phase 2: Verify 的 ExpectedChecksum / StartAddress / EndAddress 从 Segment 自动算, 避免 operator 手工填错.
        // H-2 fix: pass the step's OWN VerifyParams so each Verify step uses its own
        // CrcParameters, not the currently-selected step's (which could be a different step).
        Verify = step.Verify is { } v ? new VerifySnapshot(
            (byte)v.Algorithm,
            ExpectedChecksumFromSegment(v.SegmentIndex, v),
            StartAddressFromSegment(v.SegmentIndex),
            EndAddressFromSegment(v.SegmentIndex),
            v.SegmentIndex) : null,
        EcuReset = step.EcuReset is { } er ? new EcuResetSnapshot(er.ResetType) : null,
        CommunicationControl = step.CommunicationControl is { } cc ? new CommunicationControlSnapshot(cc.SubFunction) : null,
        DtcControl = step.DtcControl is { } dc ? new DtcControlSnapshot((byte)dc.SubFunction, dc.DtcGroup) : null,
        DependencyCheck = step.DependencyCheck is { } dc2 ? new DependencyCheckSnapshot(dc2.RoutineId) : null,
        FlashDriverDownload = step.FlashDriverDownload is { } ? new FlashDriverDownloadSnapshot() : null,

        // Backward-compat flat fields (kept for existing tests + executor).
        SecurityLevel = step.SecurityLevel,
        SecurityMode = step.SecurityMode,
        ManualKeyHex = step.ManualKeyHex,
        DllPath = step.DllPath,
        SeedLength = step.SeedLength,
        RoutineId = step.RoutineId,
        MemoryAddress = step.MemoryAddress,
        ResetType = step.ResetType,
        AutoResetOnFailure = step.AutoResetOnFailure,
    };

    /// <summary>
    /// Issue 3: 根据 SelectedCrcPresetIndex 解析出实际使用的 CrcParameters.
    /// index 0..3 对应 4 个预设; index -1 (Custom) 时使用 Verify.CrcParameters 中的自定义值.
    /// </summary>
    private PeakCan.HIL.Core.Uds.FlashPipeline.CrcParameters ResolveCrcParameters(VerifyParams verify)
    {
        var presets = PeakCan.HIL.Core.Uds.FlashPipeline.CrcParameters.Presets;
        return verify.SelectedCrcPresetIndex >= 0 && verify.SelectedCrcPresetIndex < presets.Count
            ? presets[verify.SelectedCrcPresetIndex]
            : verify.CrcParameters;  // Custom: use the manually-edited parameters.
    }

    /// <summary>
    /// Phase 2: 从 Segment 数据用选定的 CRC 算法重新算 ExpectedChecksum.
    /// Issue 3 fix: 不能直接读 seg.Crc32 (那是 parse 时用标准 CRC-32 算的), 必须用
    /// operator 在 Verify 步骤选择的 CrcParameters 重新算.
    /// </summary>
    // H-2 fix: takes the step's own VerifyParams instead of reading SelectedStep?.Verify.
    // Each Verify step must compute its ExpectedChecksum with its own CrcParameters.
    private uint ExpectedChecksumFromSegment(int index, VerifyParams verify)
    {
        var seg = SegmentAtIndex(index);
        if (seg is null) return 0;
        var parms = ResolveCrcParameters(verify);
        return Crc32.Compute(seg.Data, parms);
    }

    /// <summary>Phase 2: 从 Segment 自动算 StartAddress。无对应 segment 时回退 0。</summary>
    private uint StartAddressFromSegment(int index)
    {
        var seg = SegmentAtIndex(index);
        return seg?.StartAddress ?? 0;
    }

    /// <summary>Phase 2: 从 Segment 自动算 EndAddress。无对应 segment 时回退 0。</summary>
    private uint EndAddressFromSegment(int index)
    {
        var seg = SegmentAtIndex(index);
        return seg?.EndAddress ?? 0;
    }

    /// <summary>
    /// Issue 2: 缓存的扁平化 Segment 列表 (所有 firmware files 的 segments).
    /// ComboBox 绑定需要稳定的列表引用以正确追踪 SelectionIndex.
    /// FirmwareFiles 变更时由 SubscribeFirmwareFiles 触发刷新.
    /// </summary>
    private List<SegmentDisplayItem> _allSegments = [];

    public IReadOnlyList<SegmentDisplayItem> AllSegments => _allSegments;

    /// <summary>
    /// Issue 3: Flash Driver 解析出的 Segment 列表 (null-safe).
    /// </summary>
    public IReadOnlyList<Segment> FlashDriverSegments =>
        CurrentProfile.FlashDriver?.Segments ?? [];

    /// <summary>Issue 3: Flash Driver Segment 数量 (null-safe, 用于 Visibility 绑定).</summary>
    public int FlashDriverSegmentCount => FlashDriverSegments.Count;

    /// <summary>
    /// Phase 2: 把 profile 所有 firmware files 的 segments 摊平, 按 index 取。
    /// index 越界时返回 null, 由调用方回退 0。
    /// Issue 2: 零分配遍历 — 只在需要完整列表 (ComboBox 绑定) 时才分配。
    /// </summary>
    private Segment? SegmentAtIndex(int index)
    {
        if (index < 0) return null;
        int offset = index;
        foreach (var file in CurrentProfile.FirmwareFiles)
        {
            var segs = file.Segments;
            if (offset < segs.Count) return segs[offset];
            offset -= segs.Count;
        }
        return null;
    }

    /// <summary>
    /// Issue 3: 解析 flash driver 文件为 Segment 列表.
    /// HEX/S19 用 FirmwareFileParser 解析 (多地址段); raw binary 回退为单 Segment (地址 0).
    /// </summary>
    private static IReadOnlyList<Segment> ParseFlashDriverSegments(string path)
    {
        // M-4 fix: do not swallow parse errors. The caller (AddFlashDriver) has its own
        // try/catch that surfaces the error via StatusMessage - swallowing here would
        // silently produce an empty segment, hiding a malformed driver file from the operator.
        var file = FirmwareFileParser.ParseFile(path);
        return file.Segments;
    }

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
        // M2: unsubscribe from ConfigUpdated to prevent event-handler leaks
        // (especially in tests that construct multiple VM instances).
        if (_flashConfig is not null)
            _flashConfig.ConfigUpdated -= ApplyOdxDefaultsIfUnset;
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
    /// <summary>
    /// Handle the nullable SynchronizationContext in catch/finally blocks.
    /// When a UI SynchronizationContext is present (production WPF app), post the action
    /// via <c>Post</c> so <c>[ObservableProperty]</c> writes are marshalled to the UI
    /// thread and don't crash WPF binding with an <c>InvalidOperationException</c>.
    /// When null (test environment, no WPF Dispatcher), execute inline — there is no UI
    /// binding thread to protect.
    /// </summary>
    private static void PostOrExecute(SynchronizationContext? ctx, Action action)
    {
        if (ctx is not null)
            ctx.Post(_ => action(), null);
        else
            action();
    }

    /// <summary>
    /// Handle the nullable SynchronizationContext in catch/finally blocks.
    /// Overload taking a state parameter to avoid closure allocation on the hot path.
    /// When null ctx, <paramref name="action"/> is called with <paramref name="state"/>
    /// directly.
    /// </summary>
    private static void PostOrExecute<TState>(SynchronizationContext? ctx, Action<TState> action, TState state)
    {
        if (ctx is not null)
            ctx.Post(s => action((TState)s!), state);
        else
            action(state);
    }

    private sealed class NullLifetime : IHostApplicationLifetime
    {
        public static NullLifetime Instance { get; } = new();
        private NullLifetime() { }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>
    /// Inert <see cref="IFileDialogService"/> for callers that don't supply one (back-compat
    /// tests, non-DI construction). Returns null (user cancelled) so browse commands silently
    /// no-op instead of popping a real WPF dialog that would hang CI. Singleton — stateless.
    /// </summary>
    private sealed class NullFileDialogService : IFileDialogService
    {
        public static NullFileDialogService Instance { get; } = new();
        private NullFileDialogService() { }
        public string? ShowOpenDialog(string filter) => null;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }
}
