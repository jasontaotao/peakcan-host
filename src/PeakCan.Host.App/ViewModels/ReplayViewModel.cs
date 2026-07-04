using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;

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
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
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
    /// </summary>
    public ReplayViewModel(IReplayService service, IFileDialogService fileDialog)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _fileDialog = fileDialog ?? throw new ArgumentNullException(nameof(fileDialog));
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
