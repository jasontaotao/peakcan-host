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
    /// Unsubscribe from <see cref="IReplayService.FrameEmitted"/> and
    /// stop playback. Safe to call multiple times — the service is
    /// thread-safe and <see cref="ReplayService.Stop"/> is idempotent.
    /// </summary>
    public void Dispose()
    {
        _service.FrameEmitted -= OnFrameEmitted;
        _service.Stop();
        GC.SuppressFinalize(this);
    }
}
