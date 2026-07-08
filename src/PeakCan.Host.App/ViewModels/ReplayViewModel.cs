using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v1.4.0 MINOR Replay: binds <see cref="IReplayService"/> playback state
/// to the Replay tab UserControl. Source-generated properties and
/// commands (<c>[ObservableProperty]</c> + <c>[RelayCommand]</c> from
/// CommunityToolkit.Mvvm) keep the VM testable without WPF.
/// <para>
/// <b>Thread model:</b> <see cref="IReplayService.FrameEmitted"/> fires
/// on the timeline's timer thread (per the IReplayService XML doc). WPF
/// bindings throw when written from a non-UI thread, so the constructor
/// captures the <see cref="SynchronizationContext"/> at construction
/// time. <see cref="OnFrameEmitted"/> marshals the timestamp update back
/// onto the captured context (Post). When the VM is constructed outside
/// a WPF Application (test fixtures without STA), the captured context
/// is <c>null</c> and we fall back to a direct setter — this preserves
/// the unit-test loop where the test thread is the only one involved.
/// </para>
/// <para>
/// <b>State ownership:</b> <see cref="IsPlaying"/>, <see cref="IsPaused"/>,
/// <see cref="CurrentTimestamp"/> and <see cref="TotalDuration"/> mirror
/// the service after each command; the service stays the source of truth.
/// <see cref="IsLoaded"/> is purely UI state (drives button enable) — the
/// service does not track "has a file been loaded this session" the same
/// way.
/// </para>
/// </summary>
public sealed partial class ReplayViewModel : ObservableObject, IDisposable
{
    private readonly IReplayService _service;
    private readonly IFileDialogService _fileDialog;
    // v3.7.0 MINOR Chunk 1 T1: SHA-256 hasher used by BuildSnapshot to
    // stamp the .asc's contentHash into the bundle. Mirrors
    // TraceViewerViewModel._hasher (v3.6.4 PATCH). Optional at ctor
    // time? No — required: production wires Sha256AscContentHasher, tests
    // inject a NSubstitute that returns canned hex.
    private readonly IAscContentHasher _hasher;
    // v3.7.0 MINOR Chunk 1 T1: locator consulted during OpenSessionAsync
    // when the recorded .asc path is missing. Empty contentHash skips
    // the locator (path-only resolution path, matching v3.6.4 PATCH).
    private readonly IAscLocator _ascLocator;
    // v3.7.0 MINOR Chunk 1 T1+T2: bundle persistence. Save uses
    // .Save(snapshot, path); Open uses .Load(path) which returns null
    // for missing/corrupt bundles. Same library as the Trace Viewer
    // uses (the .tmtrace schema is shared).
    private readonly TraceSessionLibrary _library;
    // v3.7.0 MINOR Chunk 1 T2: MRU list. SaveCommand adds the saved
    // path; the Recent submenu binding (chunk 2) reads it. The
    // viewType="replay" overload is added in chunk 2 — for now we use
    // the default ("trace") which the chunk-2 implementer will swap.
    private readonly RecentSessionsService _recentSessions;
    private readonly ILogger<ReplayViewModel> _logger;
    // v3.11.0 MINOR T2 (H7): shared BuildSnapshot logic. Replay +
    // Trace VMs delegate the scalar envelope + content-hash computation
    // to this helper; VM-specific Sources / Playback / Viewports stay on
    // the caller. Sync shim BuildSnapshot() preserves back-compat with
    // the existing SessionAutoSaver.BuildSnapshot(vm) override (T3
    // refactors the auto-saver to await BuildSnapshotAsync directly).
    private readonly TraceSessionSnapshotBuilder _builder;
    // v1.4.0 MINOR Task 4 (memory I-5 follow-up): FrameEmitted fires on
    // the timeline's timer thread. We must Post the binding update back
    // to the captured UI context or WPF will throw. Null is a valid
    // captured value when the VM is constructed outside a SynchronizationContext
    // (e.g. test fixtures); in that path we fall back to direct set.
    private readonly SynchronizationContext? _syncContext;

    [ObservableProperty]
    private double _currentTimestamp;

    [ObservableProperty]
    private double _totalDuration;

    [ObservableProperty]
    private double _scrubberMaxValue;

    [ObservableProperty]
    private double _speed = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotLoaded))]
    // v3.8.1 PATCH: frame-step commands gate CanExecute on !IsPlaying;
    // without this attribute, the buttons wouldn't re-evaluate their
    // IsEnabled bindings when playback starts/stops.
    [NotifyCanExecuteChangedFor(nameof(NextFrameCommand), nameof(PrevFrameCommand))]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// v3.9.2 PATCH H1: bindable transient status string for non-error
    /// UI feedback. Currently driven by <see cref="IReplayService.LoopRewound"/>
    /// to surface "Rewind: loop region (start → end)" during A/B loop
    /// playback (the v3.9.0 MINOR P1 event contract promised this but
    /// the UI subscriber was never wired up).
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    // v3.8.1 PATCH: 5 v3.8.0 commands gate CanExecute on IsLoaded (or on
    // LoopRegions.Count which expands only after IsLoaded=true). This
    // attribute forces the source-gen partial setter to call
    // NotifyCanExecuteChanged on each listed command when IsLoaded
    // flips. Pairs with the IsEnabled bindings on the toolbar buttons
    // (ReplayView.xaml) to make the buttons visually enable/disable
    // at the right moments.
    [NotifyCanExecuteChangedFor(nameof(NextFrameCommand), nameof(PrevFrameCommand),
        nameof(AddBookmarkCommand), nameof(AddLoopRegionCommand),
        nameof(ClearLoopRegionsCommand))]
    private bool _isLoaded;

    // v1.5.0 MINOR Task 5: Loop is a proxy to the service. The CheckBox
    // binding sets Loop=true/false on the VM; the setter writes through to
    // _service.Loop and raises PropertyChanged so the binding re-reads the
    // canonical value. Source-gen generates the public Loop from this
    // backing field; we cannot replace the auto-property with a manual one
    // without breaking the generated setter, so we hook the source-gen
    // partial callback OnLoopChanged instead of using a custom setter.
    [ObservableProperty]
    private bool _loop;

    // v1.5.0 MINOR Task 5: free-form text the user types into the
    // CAN-ID filter TextBox. The OnCanIdFilterTextChanged partial
    // callback parses this string into the HashSet<uint> assigned to
    // _service.CanIdFilter.
    [ObservableProperty]
    private string _canIdFilterText = string.Empty;

    // v1.5.0 MINOR Task 5: inline error shown next to the TextBox when
    // the parser hits invalid tokens. Null when the input is valid (or
    // cleared).
    [ObservableProperty]
    private string? _canIdFilterError;

    // v1.6.1 PATCH Item 2: manual properties replace the v1.5.1
    // [ObservableProperty] source-gen pattern. SetProperty(ref, value,
    // validator) from CommunityToolkit.Mvvm 8.4+ does NOT write the
    // backing field when the validator returns false, which closes the
    // UX gap where a rejected Start > End update left the VM property
    // holding the new (invalid) value while the service retained the
    // old value. Now both stay in sync: rejected update = no field
    // change = UI TextBox reads old value via binding.

    private double? _startTimestamp;

    /// <summary>
    /// Inclusive lower bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
    /// null = unbounded below. Setter validates against <see cref="EndTimestamp"/>;
    /// rejected updates keep the prior value and surface
    /// <see cref="RangeFilterError"/>.
    /// </summary>
    public double? StartTimestamp
    {
        get => _startTimestamp;
        set
        {
            if (!IsValidRange(value, _endTimestamp))
            {
                // Rejected: do NOT touch backing field, do NOT push to
                // service. UI binding reads back the old value via the
                // unchanged getter. RangeFilterError surfaces the reason.
                RangeFilterError = "Start must be ≤ End";
                return;
            }
            if (!EqualityComparer<double?>.Default.Equals(_startTimestamp, value))
            {
                _startTimestamp = value;
                OnPropertyChanged();
            }
            _service.StartTimestamp = value;
            RangeFilterError = null;
        }
    }

    private double? _endTimestamp;

    /// <summary>
    /// Inclusive upper bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
    /// null = unbounded above. Mirrors <see cref="StartTimestamp"/> validation.
    /// </summary>
    public double? EndTimestamp
    {
        get => _endTimestamp;
        set
        {
            if (!IsValidRange(_startTimestamp, value))
            {
                RangeFilterError = "Start must be ≤ End";
                return;
            }
            if (!EqualityComparer<double?>.Default.Equals(_endTimestamp, value))
            {
                _endTimestamp = value;
                OnPropertyChanged();
            }
            _service.EndTimestamp = value;
            RangeFilterError = null;
        }
    }

    /// <summary>
    /// Range constraint validator shared by <see cref="StartTimestamp"/>
    /// and <see cref="EndTimestamp"/> setters. Returns true when at least
    /// one endpoint is null, or when start &lt;= end.
    /// </summary>
    private static bool IsValidRange(double? start, double? end)
        => !(start.HasValue && end.HasValue && start > end);

    // v1.5.1 PATCH Task 2: inline error shown next to the range
    // TextBoxes when Start > End. Null when the range is valid (or
    // both bounds are null / Start ≤ End). Single shared error
    // property for both boxes — same conceptual error class.
    [ObservableProperty]
    private string? _rangeFilterError;

    /// <summary>Inverse of <see cref="IsLoaded"/>. Convenience for XAML <c>IsEnabled</c> bindings.</summary>
    public bool IsNotLoaded => !IsLoaded;

    /// <summary>
    /// Construct the VM. Capture <see cref="SynchronizationContext.Current"/>
    /// so the <see cref="IReplayService.FrameEmitted"/> callback can
    /// marshal binding updates to the UI thread.
    /// <para>
    /// v3.7.0 MINOR Chunk 1: the ctor grew from 2 to 7 args as the
    /// Replay tab gained bundle save/load parity with the Trace
    /// Viewer. The four new deps (hasher / locator / library /
    /// recentSessions) match the Trace Viewer's DI surface; production
    /// DI wires them in <c>AppHostBuilder</c> (chunk 2 territory), tests
    /// pass either real instances (TraceSessionLibrary +
    /// RecentSessionsService to a temp path) or NSubstitute doubles.
    /// </para>
    /// </summary>
    public ReplayViewModel(
        IReplayService service,
        IFileDialogService fileDialog,
        IAscContentHasher hasher,
        IAscLocator ascLocator,
        TraceSessionLibrary library,
        RecentSessionsService recentSessions,
        ILogger<ReplayViewModel>? logger = null,
        TraceSessionSnapshotBuilder? builder = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _fileDialog = fileDialog ?? throw new ArgumentNullException(nameof(fileDialog));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _ascLocator = ascLocator ?? throw new ArgumentNullException(nameof(ascLocator));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _recentSessions = recentSessions ?? throw new ArgumentNullException(nameof(recentSessions));
        _logger = logger ?? NullLogger<ReplayViewModel>.Instance;
        // v3.11.0 MINOR T2 (H7): default to a builder wrapping the same
        // hasher so existing test ctor calls (no builder arg) keep
        // compiling. Production DI wires a singleton builder; the
        // default keeps unit-test hermeticity — no DI container required.
        _builder = builder ?? new TraceSessionSnapshotBuilder(_hasher);
        _syncContext = SynchronizationContext.Current;
        _service.FrameEmitted += OnFrameEmitted;
        // v1.4.2 PATCH Item 3: subscribe to PlaybackEnded so sink failures
        // (e.g. ReplaySendException) surface as ErrorMessage in the UI.
        // Previously no consumer subscribed to this event (Phase 2.5 finding
        // in the v1.4.2 PATCH spec).
        _service.PlaybackEnded += OnPlaybackEnded;
        // v3.9.2 PATCH H1: subscribe to LoopRewound. The v3.9.0 MINOR P1
        // contract promised "Rewind: loop region X" status feedback but
        // no UI consumer was ever wired. Now marshal the (Start, End)
        // tuple to the captured SynchronizationContext and surface via
        // StatusMessage so the user sees the rewind.
        _service.LoopRewound += OnLoopRewound;
        // v1.5.0 MINOR Task 5: seed Loop from the service so the CheckBox
        // reflects the current state at startup (and after a future
        // LoadAsync that may reset it).
        _loop = _service.Loop;
        // v3.7.0 MINOR Chunk 2: subscribe to the MRU service so the
        // Replay tab's Open Recent submenu reflects Add / Remove / Clear
        // / LoadAsync. Initial RefreshRecentEntries runs synchronously
        // — the service leaves the list empty until LoadAsync returns,
        // so an empty refresh is the correct first state.
        // v3.14.0 MINOR A3: promoted from a lambda to
        // OnRecentSessionsPropertyChanged so Dispose can -= it.
        // RecentSessionsService is a DI singleton; without -= the
        // closure chain singleton → old-VM → old-entries prevents
        // old-VM GC. Lambdas can't be -=ed by reference.
        _recentSessions.PropertyChanged += OnRecentSessionsPropertyChanged;
        RefreshRecentEntries();
    }

    /// <summary>
    /// v3.14.0 MINOR A3: handler for <see cref="RecentSessionsService"/>'s
    /// INPC. Promoted from a lambda in the ctor so Dispose can cancel
    /// the subscription. Lambdas are not referenceable from -= and
    /// would otherwise pin this VM to the singleton's lifetime.
    /// </summary>
    private void OnRecentSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => RefreshRecentEntries();

    /// <summary>
    /// FrameEmitted is invoked on the timer callback thread. We Post the
    /// timestamp update to the captured <see cref="SynchronizationContext"/>
    /// so the binding writes occur on the UI thread. Without this, WPF
    /// throws on cross-thread collection / DP access.
    /// </summary>
    private void OnFrameEmitted(ReplayFrame frame)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => CurrentTimestamp = frame.Timestamp, null);
        }
        else
        {
            // Test path: no SynchronizationContext. Direct set is safe
            // because tests don't pump the dispatcher — they assert on
            // the value immediately after raising the event.
            CurrentTimestamp = frame.Timestamp;
        }
    }

    /// <summary>
    /// v1.4.2 PATCH Item 3: invoked on the timer callback thread when
    /// playback ends (EOF or sink failure). Surfaces sink failures in
    /// <see cref="ErrorMessage"/>; on normal EOF, just resets
    /// <see cref="IsPlaying"/>. Marshals to the captured
    /// <see cref="SynchronizationContext"/> like <see cref="OnFrameEmitted"/>.
    /// </summary>
    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => ApplyPlaybackEnded(e), null);
        }
        else
        {
            // Test path: no SynchronizationContext. Direct call is safe
            // because tests assert on the state immediately after the event.
            ApplyPlaybackEnded(e);
        }
    }

    private void ApplyPlaybackEnded(PlaybackEndedEventArgs e)
    {
        if (e.Error is not null)
        {
            ErrorMessage = $"Replay aborted: {e.Error.Message}";
        }
        // Whether the end was normal (EOF) or error, stop playing.
        IsPlaying = false;
    }

    /// <summary>
    /// v3.9.2 PATCH H1: handler for
    /// <see cref="IReplayService.LoopRewound"/>. Surfaced via
    /// <see cref="StatusMessage"/> so the user sees the rewind happen
    /// during A/B loop playback. Fired on the timer-callback thread —
    /// marshal to <see cref="SynchronizationContext"/> like the
    /// sibling <see cref="OnFrameEmitted"/> / <see cref="OnPlaybackEnded"/>
    /// handlers.
    /// </summary>
    private void OnLoopRewound(object? sender, LoopRegionRewoundEventArgs e)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => StatusMessage = $"Rewind: loop region ({e.Start:F2}s → {e.End:F2}s)", null);
        }
        else
        {
            // Test path: no SynchronizationContext. Direct call is safe
            // because tests assert on the state immediately after the event.
            StatusMessage = $"Rewind: loop region ({e.Start:F2}s → {e.End:F2}s)";
        }
    }

    /// <summary>
    /// Unsubscribe from <see cref="IReplayService.FrameEmitted"/> and
    /// stop playback. Safe to call multiple times — the service is
    /// thread-safe and <see cref="ReplayService.Stop"/> is idempotent.
    /// <para>
    /// v3.14.0 MINOR A2: cancel the v3.9.0 MINOR P1 LoopRewound subscription.
    /// <see cref="IReplayService"/> is a DI singleton, so without the -=
    /// the closure chain singleton → old-VM → old-frames prevents
    /// old-VM GC.
    /// </para>
    /// <para>
    /// v3.14.0 MINOR A3: cancel the <see cref="RecentSessionsService"/>
    /// PropertyChanged subscription. The lambda in the ctor was promoted
    /// to <see cref="OnRecentSessionsPropertyChanged"/> so Dispose can
    /// -= it (lambdas can't be -=ed by reference). RecentSessionsService
    /// is a DI singleton so without the -= the closure chain pins the VM.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _service.LoopRewound -= OnLoopRewound;
        _service.FrameEmitted -= OnFrameEmitted;
        _service.PlaybackEnded -= OnPlaybackEnded;
        // v3.14.0 MINOR A3: cancel the RecentSessionsService.PropertyChanged
        // subscription. Matches the += in the ctor (promoted from lambda).
        _recentSessions.PropertyChanged -= OnRecentSessionsPropertyChanged;
        _service.Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// v3.8.0 MINOR chunk 4: VM-side projection of <see cref="BookmarkDto"/>
/// for the Replay tab's bookmark panel. Lives nested outside
/// <see cref="ReplayViewModel"/> because <see cref="ReplayViewModel.Bookmarks"/>
/// exposes <c>ObservableCollection&lt;BookmarkVm&gt;</c> as a public type
/// and a less-accessible element on a public collection trips CS0053
/// (same pattern as <see cref="ReplayViewModel.RecentSessionVm"/>).
/// </summary>
public sealed record BookmarkVm(BookmarkDto Dto)
{
    public string Id => Dto.Id;
    public double Timestamp => Dto.Timestamp;
    // v3.9.0 MINOR P4: make Label a get/set property (was get-only
    // before P4) so a WPF DataGrid's CellEditingTemplate TextBox can
    // TwoWay-bind to it. The setter forwards to Dto.Label so the
    // underlying DTO is updated in-place; the bundle serializer then
    // persists the new label on next Save. The get path still reads
    // through Dto.Label so a one-shot Dto edit (e.g. via direct
    // record-with mutation) is reflected immediately.
    public string? Label
    {
        get => Dto.Label;
        set => Dto.Label = value;
    }
    public string Display => Label is { Length: > 0 }
        ? $"{Dto.Timestamp:F3}s — {Label}"
        : $"{Dto.Timestamp:F3}s";
}

/// <summary>
/// v3.8.0 MINOR chunk 6: VM-side projection of <see cref="LoopRegionDto"/>
/// for the Replay tab's loop-regions panel. Same nested-public-record
/// pattern as <see cref="BookmarkVm"/> (CS0053 fix).
/// </summary>
public sealed record LoopRegionVm(LoopRegionDto Dto)
{
    public string Id => Dto.Id;
    public double Start => Dto.Start;
    public double End => Dto.End;
    public string? Label => Dto.Label;
    public string Display => Label is { Length: > 0 }
        ? $"[{Dto.Start:F3} – {Dto.End:F3}] {Label}"
        : $"[{Dto.Start:F3} – {Dto.End:F3}]";
}