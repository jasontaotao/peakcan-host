using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
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
    /// v3.7.0 MINOR Chunk 2: VM-side projection of
    /// <see cref="RecentSessionDto"/> for the Replay tab's Open Recent
    /// submenu. <see cref="Path"/> is the CommandParameter for
    /// <see cref="OpenRecentSessionCommand"/>; <see cref="Label"/> is the
    /// menu header text. Lives nested inside
    /// <see cref="ReplayViewModel"/> because the Replay tab owns its own
    /// filter (replay entries only) — mirroring
    /// <see cref="AppShellViewModel.RecentSessionVm"/> keeps the two
    /// surfaces symmetric. Declared public because
    /// <see cref="RecentSessionEntries"/> exposes
    /// <c>ObservableCollection&lt;RecentSessionVm&gt;</c> as a public
    /// type, and a less-accessible element on a public collection
    /// trips CS0053.
    /// </summary>
    public sealed record RecentSessionVm(string Path, string Label);

    /// <summary>
    /// v3.8.0 MINOR chunk 4: in-memory list of bookmarks the user has
    /// captured at the current playback cursor. Persisted to the
    /// bundle via <see cref="BundlePlaybackDto.Bookmarks"/> in chunk 7.
    /// Owned by the Replay tab (the Trace Viewer doesn't need bookmarks
    /// because it loads N traces; Replay loads one).
    /// </summary>
    public ObservableCollection<BookmarkVm> Bookmarks { get; } = new();

    /// <summary>
    /// v3.8.0 MINOR chunk 6: in-memory list of named playback windows.
    /// Persisted to the bundle via
    /// <see cref="BundlePlaybackDto.LoopRegions"/> in chunk 7. When
    /// non-empty and <see cref="Loop"/> is true, the FIRST region
    /// overrides <see cref="IReplayService.StartTimestamp"/> /
    /// <see cref="IReplayService.EndTimestamp"/> for wrap-around (full
    /// A/B rewind is deferred to v3.9.0).
    /// </summary>
    public ObservableCollection<LoopRegionVm> LoopRegions { get; } = new();

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: XAML binding source for the Replay tab's
    /// Open Recent submenu. Rebuilt from
    /// <see cref="RecentSessionsService.Recent"/> (filtered to replay
    /// entries) whenever the service raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// (Add / Remove / Clear / LoadAsync). Visible to the
    /// <c>RecentSessionEntries</c> property setter — CommunityToolkit.Mvvm
    /// requires the backing field to be private.
    /// </summary>
    public ObservableCollection<RecentSessionVm> RecentSessionEntries { get; } = new();

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
        ILogger<ReplayViewModel>? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _fileDialog = fileDialog ?? throw new ArgumentNullException(nameof(fileDialog));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _ascLocator = ascLocator ?? throw new ArgumentNullException(nameof(ascLocator));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _recentSessions = recentSessions ?? throw new ArgumentNullException(nameof(recentSessions));
        _logger = logger ?? NullLogger<ReplayViewModel>.Instance;
        _syncContext = SynchronizationContext.Current;
        _service.FrameEmitted += OnFrameEmitted;
        // v1.4.2 PATCH Item 3: subscribe to PlaybackEnded so sink failures
        // (e.g. ReplaySendException) surface as ErrorMessage in the UI.
        // Previously no consumer subscribed to this event (Phase 2.5 finding
        // in the v1.4.2 PATCH spec).
        _service.PlaybackEnded += OnPlaybackEnded;
        // v1.5.0 MINOR Task 5: seed Loop from the service so the CheckBox
        // reflects the current state at startup (and after a future
        // LoadAsync that may reset it).
        _loop = _service.Loop;
        // v3.7.0 MINOR Chunk 2: subscribe to the MRU service so the
        // Replay tab's Open Recent submenu reflects Add / Remove / Clear
        // / LoadAsync. Initial RefreshRecentEntries runs synchronously
        // — the service leaves the list empty until LoadAsync returns,
        // so an empty refresh is the correct first state.
        _recentSessions.PropertyChanged += (_, __) => RefreshRecentEntries();
        RefreshRecentEntries();
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: rebuild <see cref="RecentSessionEntries"/>
    /// from <see cref="RecentSessionsService.Recent"/> filtered to
    /// entries with <see cref="RecentSessionDto.ViewType"/> ==
    /// <c>"replay"</c>. Mirrors
    /// <see cref="AppShellViewModel.RefreshRecentEntries"/> which
    /// filters to <c>"trace"</c> / legacy <c>""</c>. Cheap (max 5
    /// entries) — full Clear + rebuild avoids the per-item
    /// CollectionChanged dance. Called on
    /// <see cref="RecentSessionsService"/> PropertyChanged (any mutation).
    /// </summary>
    private void RefreshRecentEntries()
    {
        RecentSessionEntries.Clear();
        foreach (var r in _recentSessions.Recent)
        {
            if (r.ViewType != "replay") continue;
            RecentSessionEntries.Add(new RecentSessionVm(r.Path, r.Label));
        }
    }

    /// <summary>
    /// v1.5.0 MINOR Task 5: Loop source-gen partial callback. Forward
    /// the new value to the underlying <see cref="IReplayService.Loop"/>
    /// so the timeline actually starts looping (or stops). The service
    /// is the source of truth for playback behavior.
    /// </summary>
    partial void OnLoopChanged(bool value)
    {
        if (_service.Loop != value)
        {
            _service.Loop = value;
        }
    }

    /// <summary>
    /// v1.5.0 MINOR Task 5: CanIdFilterText source-gen partial callback.
    /// Parses the free-form text into a <see cref="HashSet{T}"/> of CAN
    /// IDs and pushes it onto <see cref="IReplayService.CanIdFilter"/>.
    /// <para>
    /// v3.4.4 PATCH: delegation refactor. The lexer moved to the shared
    /// <see cref="CanIdListParser"/> in Core; this method now just
    /// forwards the result to the service + surfaces
    /// <see cref="CanIdFilterError"/> when there are invalid tokens.
    /// </para>
    /// <para>
    /// Token syntax: comma- or whitespace-separated. Each token is
    /// trimmed. <c>0x</c> / <c>0X</c> prefix means hex; otherwise
    /// decimal. Empty / whitespace input clears the filter to
    /// <c>null</c> (all frames pass). Invalid tokens are collected and
    /// surfaced via <see cref="CanIdFilterError"/> without discarding
    /// the valid ones, so a single typo doesn't wipe the user's work.
    /// </para>
    /// </summary>
    partial void OnCanIdFilterTextChanged(string value)
    {
        var result = CanIdListParser.Parse(value);
        _service.CanIdFilter = result.AllowList;
        CanIdFilterError = result.HasInvalidTokens
            ? $"Invalid token(s): {string.Join(", ", result.InvalidTokens)}"
            : null;
    }

    /// <summary>
    /// Open an ASC file via <see cref="IFileDialogService.ShowOpenDialog"/>,
    /// load it through <see cref="IReplayService.LoadAsync"/>, and copy
    /// the parsed duration into the slider's max value. Catches
    /// <see cref="ReplayException"/> (load + format) and surfaces the
    /// message via <see cref="ErrorMessage"/>; any other exception
    /// propagates so the WPF host can decide how to handle it.
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            ErrorMessage = null;
            var path = _fileDialog.ShowOpenDialog(filter: "ASC files (*.asc)|*.asc");
            if (path is null) return; // user cancelled
            await _service.LoadAsync(path).ConfigureAwait(true);
            LoadedFilePath = path;
            TotalDuration = _service.TotalDuration;
            ScrubberMaxValue = TotalDuration;
            CurrentTimestamp = 0.0;
            IsLoaded = true;
            // v1.5.1 PATCH Task 2 (Decision 5): clear the range filter
            // after a successful load. A new file's timestamps are
            // unlikely to match the prior bounds (e.g. old End=60 on
            // a 5-second file silently filters out everything). Unlike
            // CanIdFilter, CAN IDs are content-stable across files, so
            // the ID filter is intentionally NOT cleared.
            StartTimestamp = null;
            EndTimestamp = null;
            RangeFilterError = null;
        }
        catch (ReplayException ex)
        {
            ErrorMessage = ex.Message;
            // Clear load state on failure — UI greys out transport bar.
            IsLoaded = false;
            LoadedFilePath = null;
            TotalDuration = 0.0;
            ScrubberMaxValue = 0.0;
        }
    }

    /// <summary>
    /// Start playback. Mirrors the resulting service state into the
    /// bindable <see cref="IsPlaying"/>/<see cref="IsPaused"/> flags.
    /// The service's <see cref="IReplayService.State"/> is authoritative.
    /// </summary>
    [RelayCommand]
    private void Play()
    {
        _service.Play();
        IsPlaying = _service.State == ReplayState.Playing;
        IsPaused = false;
    }

    /// <summary>
    /// Pause playback. Updates <see cref="IsPlaying"/>/<see cref="IsPaused"/>
    /// directly — <see cref="ReplayState.Paused"/> is the only legal post-state.
    /// </summary>
    [RelayCommand]
    private void Pause()
    {
        _service.Pause();
        IsPlaying = false;
        IsPaused = true;
    }

    /// <summary>
    /// Stop playback and reset the timeline cursor to t=0. Clears
    /// <see cref="IsPlaying"/>/<see cref="IsPaused"/> and snaps
    /// <see cref="CurrentTimestamp"/> back so the slider thumb jumps to the start.
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        _service.Stop();
        IsPlaying = false;
        IsPaused = false;
        CurrentTimestamp = 0.0;
    }

    /// <summary>
    /// Jump to an absolute timestamp. Updates
    /// <see cref="CurrentTimestamp"/> immediately so the slider thumb
    /// tracks without waiting for the next timer tick.
    /// </summary>
    [RelayCommand]
    private void SeekTo(double timestamp)
    {
        _service.Seek(timestamp);
        CurrentTimestamp = timestamp;
    }

    /// <summary>
    /// Change playback speed multiplier. Guards against non-positive
    /// values per the <see cref="IReplayService.SetSpeed"/> contract —
    /// a 0 / negative multiplier would divide-by-zero the timeline.
    /// </summary>
    [RelayCommand]
    private void SetSpeed(double multiplier)
    {
        if (multiplier <= 0) return;
        _service.SetSpeed(multiplier);
        Speed = multiplier;
    }

    // -------- v3.7.0 MINOR Chunk 1: bundle save/load --------

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T1: collect the current Replay session
    /// state into a <see cref="TraceSessionBundleDto"/>. Mirrors
    /// <see cref="TraceViewerViewModel.BuildSnapshot"/> but the
    /// Replay tab is single-source — exactly one entry in
    /// <see cref="TraceSessionBundleDto.Sources"/> (path + hash). The
    /// DBC / global filter / per-source filter / per-source viewport
    /// fields are all Trace-Viewer-specific and stay at their defaults.
    /// The playback envelope captures Loop / Speed / scrubber + the
    /// range filter (Start/End) so the cursor lands at the same
    /// timestamp on reload. <see cref="CanIdFilterText"/> is captured
    /// via the new <see cref="BundlePlaybackDto.ReplayCanIdFilterText"/>
    /// field (chunk 2 documents the schema).
    /// <para>
    /// The contentHash is computed synchronously via
    /// <c>GetAwaiter().GetResult()</c> on the hasher — same pattern
    /// the Trace Viewer uses (v3.6.4 PATCH). For typical .asc files
    /// (10–500 MB) the SHA-256 round-trip is &lt; 2 s; BuildSnapshot
    /// is invoked from a <c>Task.Run</c>-wrapped save on the
    /// SaveCommand path, so the UI thread is not blocked.
    /// </para>
    /// </summary>
    public TraceSessionBundleDto BuildSnapshot()
    {
        var hash = "";
        if (!string.IsNullOrEmpty(LoadedFilePath) && File.Exists(LoadedFilePath))
        {
            try
            {
                hash = _hasher.ComputeAsync(LoadedFilePath).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Hashing failed (locked file / ACL). Skip — the
                // bundle still saves with contentHash="" and the
                // path-only resolution covers it on reload.
                LogHashFailed(_logger, ex, LoadedFilePath);
                hash = "";
            }
        }
        var displayName = string.IsNullOrEmpty(LoadedFilePath)
            ? ""
            : Path.GetFileNameWithoutExtension(LoadedFilePath);
        var sourceId = Guid.NewGuid().ToString("N");
        var dto = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = GetAppVersion(),
            DbcPath = "",
            GlobalCanIdFilter = "",
        };
        dto.Sources = new List<BundleSourceDto>
        {
            new()
            {
                SourceId = sourceId,
                DisplayName = displayName,
                Path = LoadedFilePath ?? "",
                ColorA = 0,
                ColorR = 0,
                ColorG = 0,
                ColorB = 0,
                StrokeStyle = "Solid",
                CanIdFilter = "",
                ContentHash = hash,
            },
        };
        dto.Playback = new BundlePlaybackDto
        {
            MasterSourceId = "",
            Loop = Loop,
            Speed = Speed,
            ScrubberValue = CurrentTimestamp,
            StartTimestamp = StartTimestamp,
            EndTimestamp = EndTimestamp,
            ReplayCanIdFilterText = CanIdFilterText ?? "",
            // v3.8.0 MINOR chunk 7: persist in-memory bookmarks +
            // loop-regions. Empty list (not null) is intentional — keeps
            // the bundle shape stable across v3.7.2 readers who deserialize
            // the optional field as null and treat null-vs-empty as
            // semantically the same ("no bookmarks / regions").
            Bookmarks = Bookmarks.Select(b => b.Dto).ToList(),
            LoopRegions = LoopRegions.Select(r => r.Dto).ToList(),
        };
        // Replay has no per-series viewports (single source, no
        // chart subplots). Empty list keeps the envelope shape stable
        // with the Trace Viewer.
        dto.Viewports = new List<BundleViewportDto>();
        return dto;
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T1: restore a saved Replay session from
    /// a <c>.tmtrace</c> bundle. The path-only resolution matches
    /// <see cref="TraceViewerViewModel.OpenSessionAsync"/>: if the
    /// recorded <c>.asc</c> is missing and the bundle carries a
    /// non-empty contentHash, ask <see cref="IAscLocator"/> for a
    /// relocated copy and retry the load. Reload also lands on a
    /// paused cursor (IsPlaying=false) — never auto-resumes.
    /// <para>
    /// Caller (View) handles the open-file dialog and the
    /// missing-ascs MessageBox UX — the VM returns the list of paths
    /// that could not be resolved so the View can surface them.
    /// </para>
    /// </summary>
    /// <returns>List of source .asc paths that did NOT resolve on load.
    /// Empty when the bundle had no sources or when every source
    /// resolved cleanly.</returns>
    public async Task<IReadOnlyList<string>> OpenSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        var dto = await Task.Run(() => _library.Load(path)).ConfigureAwait(true);
        if (dto is null) return Array.Empty<string>();

        var missing = new List<string>();
        // Each bundle is expected to be single-source. We iterate the
        // list defensively in case a hand-edited bundle (or a future
        // multi-source Replay tab) supplies > 1.
        foreach (var bs in dto.Sources)
        {
            var loadPath = bs.Path;
            // Hash-based relocation: skip the locator for empty hashes
            // (path-only resolution). The locator itself short-circuits
            // on empty input too, so this is just a tiny perf save.
            if (!string.IsNullOrEmpty(bs.Path) &&
                !File.Exists(bs.Path) &&
                !string.IsNullOrEmpty(bs.ContentHash))
            {
                var relocated = await _ascLocator.LocateAsync(bs.ContentHash).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(relocated) && File.Exists(relocated))
                {
                    LogRelocated(_logger, bs.Path, relocated);
                    loadPath = relocated;
                }
            }
            try
            {
                await _service.LoadAsync(loadPath).ConfigureAwait(true);
                // Mirror the successful load into the bindable surface
                // — same fields OpenAsync populates.
                LoadedFilePath = loadPath;
                TotalDuration = _service.TotalDuration;
                ScrubberMaxValue = TotalDuration;
                IsLoaded = true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                LogSourceMissing(_logger, bs.Path, ex);
                missing.Add(bs.Path);
            }
            catch (ReplayException ex)
            {
                LogSourceMissing(_logger, bs.Path, ex);
                missing.Add(bs.Path);
            }
        }
        // 2. Apply playback state if present. Always to a paused
        //    cursor — never auto-resume on session reload.
        // v3.8.3 PATCH H1: failure teardown. When ANY source in a
        // multi-source bundle fails to load (or ALL sources fail),
        // mirror OpenAsync's ReplayException handling (line 418-422) —
        // clear the bindable surface so the UI doesn't show a
        // misleading "loaded with error banner" state, and SKIP the
        // playback-envelope restore (bundle state is meaningless
        // without the sources it references).
        if (missing.Count > 0)
        {
            IsLoaded = false;
            LoadedFilePath = null;
            TotalDuration = 0.0;
            ScrubberMaxValue = 0.0;
        }
        else if (dto.Playback is { } pb)
        {
            // v3.8.2 PATCH: CurrentTimestamp (playback cursor) used
            // to be restored inside the per-source loop above. When a
            // bundle had zero sources (e.g. a hand-edited bundle or a
            // future Source-path-empty variant), the loop never ran
            // and CurrentTimestamp stayed at 0 — silent clobber of
            // the saved cursor. Move to the post-loop block alongside
            // the other scalar playback fields.
            CurrentTimestamp = pb.ScrubberValue;
            Loop = pb.Loop;
            Speed = pb.Speed <= 0 ? 1.0 : pb.Speed;
            StartTimestamp = pb.StartTimestamp;
            EndTimestamp = pb.EndTimestamp;
            // ReplayCanIdFilterText is the Replay-tab filter field;
            // setting it on the VM fires the OnCanIdFilterTextChanged
            // partial callback which parses + pushes to the service.
            // Empty / missing playback envelope → leave the existing
            // filter alone (no clobber on bundles from before this
            // field existed).
            CanIdFilterText = pb.ReplayCanIdFilterText ?? "";

            // v3.8.0 MINOR chunk 7: restore bookmarks + loop regions.
            // Old bundles (no Bookmarks/LoopRegions keys) deserialize as
            // null → clear to empty. Same null-vs-empty handling as
            // BuildSnapshot: empty list == no items.
            // v3.8.3 PATCH M1: validate on restore. Bookmarks with
            // negative timestamps are unreachable from binary-search
            // frame-step (strict > / <), so filter them out — a
            // hand-edited bundle could put a bookmark at t=-1.0; we
            // silently drop it. Loop regions with End < Start are
            // normalized to a 1-second window (same fallback
            // AddLoopRegion uses at the creation site) — preserves
            // the original Id + Label for future click-to-jump UX.
            Bookmarks.Clear();
            if (pb.Bookmarks is not null)
            {
                foreach (var b in pb.Bookmarks)
                {
                    if (b.Timestamp < 0) continue;
                    Bookmarks.Add(new BookmarkVm(b));
                }
            }
            LoopRegions.Clear();
            if (pb.LoopRegions is not null)
            {
                foreach (var r in pb.LoopRegions)
                {
                    var start = r.Start;
                    var end = r.End < start ? start + 1.0 : r.End;
                    var normalized = new LoopRegionDto(r.Id, start, end, r.Label);
                    LoopRegions.Add(new LoopRegionVm(normalized));
                }
            }
        }
        IsPlaying = false;
        IsPaused = false;
        return missing;
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T2: save the current Replay session to
    /// a <c>.tmtrace</c> bundle. The command itself pops the save
    /// dialog (different from the Trace Viewer, which takes the path
    /// as an argument so the View can pop the dialog). The dialog
    /// service is injected via ctor — testable with a fake.
    /// <para>
    /// <b>Threading:</b> the snapshot is built inline (fast) and the
    /// disk write is wrapped in <c>Task.Run</c> to avoid blocking the
    /// UI thread on the atomic-rename I/O.
    /// </para>
    /// <para>
    /// v3.7.0 MINOR Chunk 2: records the saved path in the MRU list
    /// with <c>viewType: "replay"</c> so the Replay tab's Recent
    /// submenu filters to its own entries (and never sees the
    /// Trace Viewer's MRU list).
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var path = _fileDialog.ShowSaveDialog(
            filter: "Trace Viewer session|*.tmtrace;*.TMTRACE|All files|*.*",
            defaultExt: ".tmtrace",
            initialDirectory: null);
        if (string.IsNullOrEmpty(path)) return;  // user cancelled
        var snapshot = BuildSnapshot();
        await Task.Run(() => _library.Save(snapshot, path)).ConfigureAwait(true);
        _recentSessions.Add(path, viewType: "replay");
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T2: open a Replay session from a
    /// <c>.tmtrace</c> bundle. Pops the open dialog, forwards the
    /// path to <see cref="OpenSessionAsync"/>. Mirrors
    /// <see cref="TraceViewerViewModel.OpenSessionAsync"/> but the
    /// Replay tab does NOT need a per-source missing-ascs MessageBox
    /// (the bundle is always single-source + the Reload + Replay
    /// loop lets the user pick a relocated file interactively).
    /// </summary>
    [RelayCommand]
    private async Task OpenSession()
    {
        var path = _fileDialog.ShowOpenDialog(
            filter: "Trace Viewer session|*.tmtrace;*.TMTRACE|All files|*.*");
        if (string.IsNullOrEmpty(path)) return;  // user cancelled
        var missing = await OpenSessionAsync(path).ConfigureAwait(true);
        if (missing.Count > 0)
        {
            // Mirror the AppShell's pattern (AppShellViewModel.cs:
            // OpenSessionAsync). We don't have WPF access here; the
            // chunk-2 UI work is responsible for hooking the
            // MessageBox. The VM still surfaces the missing list so
            // the View can decide what to do.
            ErrorMessage = $"{missing.Count} .asc file(s) could not be located. Use File → Open .asc to reload the source.";
        }
    }

    // -------- v3.7.0 MINOR Chunk 2: Recent submenu wiring --------

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: Replay tab's Open Recent menu command.
    /// Loads the chosen bundle through
    /// <see cref="OpenSessionAsync"/>, surfaces any missing
    /// <c>.asc</c> source via <see cref="ErrorMessage"/>, and
    /// re-records the path with <c>viewType: "replay"</c> so a
    /// re-click moves it back to the top of the list (standard MRU
    /// UX). The command is invoked from the code-behind
    /// <c>OnOpenRecentClick</c> handler that builds a
    /// <c>ContextMenu</c> from <see cref="RecentSessionEntries"/>.
    /// </summary>
    [RelayCommand]
    private async Task OpenRecentSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var missing = await OpenSessionAsync(path).ConfigureAwait(true);
        if (missing.Count > 0)
        {
            ErrorMessage = $"{missing.Count} .asc file(s) could not be located. Use File → Open .asc to reload the source.";
        }
        _recentSessions.Add(path, viewType: "replay");
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: Replay tab's Clear Recent menu command.
    /// Drops replay entries only via
    /// <see cref="RecentSessionsService.Clear(string)"/>; the AppShell's
    /// Trace entries and any future viewType's entries survive.
    /// </summary>
    [RelayCommand]
    private void ClearRecentSessions() => _recentSessions.Clear("replay");

    // ---------- v3.8.0 MINOR chunk 2: frame stepping ----------

    /// <summary>
    /// v3.8.0 MINOR chunk 2: advance the cursor to the first frame whose
    /// timestamp is strictly greater than <see cref="IReplayService.CurrentTimestamp"/>.
    /// Uses <see cref="IReplayService.Frames"/> + binary search (O(log n))
    /// rather than a new <c>Seek(int)</c> overload — reuses
    /// <see cref="IReplayService.Seek(double)"/> unchanged. Binary search uses
    /// strict <c>&gt;</c> so stepping AT a frame's timestamp advances PAST it
    /// (intuitive "next" semantic — keybind Right).
    /// <para>
    /// Guarded against playing state (see <see cref="CanStepFrame"/>) so a
    /// step+play race doesn't fight the timer thread; the user pauses to step.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStepFrame))]
    private void NextFrame()
    {
        var frames = _service.Frames;
        if (frames.Count == 0) return;
        var current = _service.CurrentTimestamp;
        int idx = BinarySearchFirstGreater(frames, current);
        if (idx < 0) return;
        _service.Seek(frames[idx].Timestamp);
    }

    /// <summary>
    /// v3.8.0 MINOR chunk 2: mirror of <see cref="NextFrame"/> moving to the
    /// last frame strictly before <see cref="IReplayService.CurrentTimestamp"/>.
    /// Binary search uses strict <c>&lt;</c> — stepping back from the first
    /// frame's timestamp is a no-op (intuitive). Keybind Left.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStepFrame))]
    private void PrevFrame()
    {
        var frames = _service.Frames;
        if (frames.Count == 0) return;
        var current = _service.CurrentTimestamp;
        int idx = BinarySearchLastLess(frames, current);
        if (idx < 0) return;
        _service.Seek(frames[idx].Timestamp);
    }

    private bool CanStepFrame()
        => IsLoaded && _service.Frames.Count > 0 && !IsPlaying;

    /// <summary>
    /// Binary search: returns the lowest index i in <paramref name="frames"/>
    /// such that <c>frames[i].Timestamp &gt; t</c>, or <c>-1</c> if no such
    /// frame exists (caller is at-or-past the last frame).
    /// </summary>
    private static int BinarySearchFirstGreater(IReadOnlyList<ReplayFrame> frames, double t)
    {
        int lo = 0, hi = frames.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (frames[mid].Timestamp > t) { best = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return best;
    }

    /// <summary>
    /// Binary search: returns the highest index i in <paramref name="frames"/>
    /// such that <c>frames[i].Timestamp &lt; t</c>, or <c>-1</c> if no such
    /// frame exists (caller is at-or-before the first frame).
    /// </summary>
    private static int BinarySearchLastLess(IReadOnlyList<ReplayFrame> frames, double t)
    {
        int lo = 0, hi = frames.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (frames[mid].Timestamp < t) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    // ---------- v3.8.0 MINOR chunk 4: bookmarks ----------

    /// <summary>
    /// v3.8.0 MINOR chunk 4: capture the current playback cursor as a
    /// bookmark. Generates a fresh GUID id and pushes a
    /// <see cref="BookmarkVm"/> onto <see cref="Bookmarks"/>. Label
    /// starts null — a future v3.9.0 PATCH may add inline label editing.
    /// Keybind Ctrl+B (added in chunk 5).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddBookmark))]
    private void AddBookmark()
    {
        var dto = new BookmarkDto(
            Guid.NewGuid().ToString("N"),
            _service.CurrentTimestamp,
            null);
        Bookmarks.Add(new BookmarkVm(dto));
    }

    private bool CanAddBookmark() => IsLoaded;

    // ---------- v3.8.0 MINOR chunk 6: loop regions ----------

    /// <summary>
    /// v3.8.0 MINOR chunk 6: capture the current Start/End range filter
    /// as a named loop region. If End is null or &lt;= Start (a degenerate
    /// range), the region is widened to a 1-second window starting at
    /// Start so the LoopRegions list never contains an invalid range.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddLoopRegion))]
    private void AddLoopRegion()
    {
        var start = _service.StartTimestamp ?? 0.0;
        var end = _service.EndTimestamp ?? start + 1.0;
        if (end <= start) end = start + 1.0;
        var dto = new LoopRegionDto(
            Guid.NewGuid().ToString("N"),
            start,
            end,
            null);
        LoopRegions.Add(new LoopRegionVm(dto));
        // v3.8.3 PATCH H2: ObservableCollection<T>.Add() doesn't fire
        // PropertyChanged for Count, so the source-gen attribute on
        // _isLoaded can't see this mutation. Notify explicitly to keep
        // the toolbar Clear button enabled-state in sync after an Add.
        // (Clear itself already notifies — see ClearLoopRegions body.)
        ClearLoopRegionsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearLoopRegions))]
    private void ClearLoopRegions()
    {
        LoopRegions.Clear();
        // v3.8.1 PATCH: ObservableCollection<T> mutations don't fire
        // PropertyChanged for the count, so the source-gen
        // CanExecuteChangedFor attribute can't see LoopRegions.Count
        // changes. Notify explicitly to keep the toolbar Clear button
        // enabled-state in sync after a Clear.
        ClearLoopRegionsCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddLoopRegion() => IsLoaded;
    private bool CanClearLoopRegions() => LoopRegions.Count > 0;

    [LoggerMessage(Level = LogLevel.Warning, Message = "BuildSnapshot: hashing failed for {Path}; bundle saved without contentHash")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle source missing or unreadable: {Path}")]
    private static partial void LogSourceMissing(ILogger logger, string? path, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle source relocated via content hash: {OldPath} -> {NewPath}")]
    private static partial void LogRelocated(ILogger logger, string? oldPath, string? newPath);

    /// <summary>
    /// v3.6.0 MINOR T1.A pattern: read version from assembly metadata
    /// instead of a hardcoded string. Mirrors
    /// <see cref="TraceViewerViewModel.GetAppVersion"/>. Strip a
    /// trailing <c>+git&lt;sha&gt;</c> suffix that LocalBuilder adds so
    /// the bundle round-trips cleanly across builds.
    /// </summary>
    private static string GetAppVersion()
    {
        var info = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

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
    /// Unsubscribe from <see cref="IReplayService.FrameEmitted"/> and
    /// stop playback. Safe to call multiple times — the service is
    /// thread-safe and <see cref="ReplayService.Stop"/> is idempotent.
    /// </summary>
    public void Dispose()
    {
        _service.FrameEmitted -= OnFrameEmitted;
        _service.PlaybackEnded -= OnPlaybackEnded;
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
    public string? Label => Dto.Label;
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
