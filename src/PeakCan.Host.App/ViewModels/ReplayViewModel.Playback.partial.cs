using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ReplayViewModel
{
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
    /// <para>
    /// v3.8.4 PATCH L1: clamps <c>timestamp</c> to
    /// <c>[0, _service.TotalDuration]</c> before forwarding to the
    /// service. The WPF slider's <c>TwoWay</c> binding can push values
    /// outside this range (drag-past-max, programmatic, numeric-text
    /// entry, momentary desync during an <c>IsLoaded</c> flip). Passing
    /// the raw value through leaves the VM reporting a position the
    /// timeline could never emit from (slider thumb at MAX, playback
    /// silent for unbounded time). Mirrors the <see cref="SetSpeed"/>
    /// non-positive-multiplier guard — the VM owns input validation to
    /// keep the service free of out-of-range concerns.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void SeekTo(double timestamp)
    {
        var clamped = Math.Clamp(timestamp, 0.0, _service.TotalDuration);
        _service.Seek(clamped);
        CurrentTimestamp = clamped;
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
}