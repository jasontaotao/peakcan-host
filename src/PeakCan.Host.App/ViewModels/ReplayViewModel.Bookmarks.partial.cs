using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ReplayViewModel
{
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
        // v3.9.0 MINOR P1: the most-recently-added LoopRegion becomes
        // the active one (auto-rewind target). v3.9.0 P1 ships the
        // "last-Add wins" activation policy — click-to-activate UX
        // (right-click row → Set Active) is deferred to v3.9.0 P3
        // (Slider visual markers) or v3.10.0. Until then, the
        // operator's most recent AddRegion is the one that loops.
        _service.ActiveLoopRegion = (dto.Start, dto.End);
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
        // v3.9.0 MINOR P1: clearing the regions also clears the
        // service-side active region so playback no longer rewinds.
        _service.ActiveLoopRegion = null;
        // v3.8.1 PATCH: ObservableCollection<T> mutations don't fire
        // PropertyChanged for the count, so the source-gen
        // CanExecuteChangedFor attribute can't see LoopRegions.Count
        // changes. Notify explicitly to keep the toolbar Clear button
        // enabled-state in sync after a Clear.
        ClearLoopRegionsCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddLoopRegion() => IsLoaded;
    private bool CanClearLoopRegions() => LoopRegions.Count > 0;
}