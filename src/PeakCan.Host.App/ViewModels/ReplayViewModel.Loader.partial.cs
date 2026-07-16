using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ReplayViewModel
{
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
    /// Open an ASC file via <see cref="IFileDialogService.ShowOpenDialog"/>,
    /// load it through <see cref="IReplayService.LoadAsync"/>, and copy
    /// the parsed duration into the slider's max value. Catches
    /// <see cref="ReplayException"/> (load + format) and surfaces the
    /// message via <see cref="ErrorMessage"/>; any other exception
    /// propagates so the WPF host can decide how to handle it.
    /// <para>
    /// v3.8.4 PATCH H1: also catches <see cref="OperationCanceledException"/>
    /// (e.g. app-shutdown CTS cancel propagates through the parse, or a
    /// user-initiated parse is pre-empted by a different command) with
    /// the same teardown shape as <see cref="ReplayException"/> but
    /// without populating <c>ErrorMessage</c> — a cancel is not a
    /// user-hostile failure. Pre-fix, the catch only matched
    /// <c>ReplayException</c>; <c>OperationCanceledException</c>
    /// propagated uncaught through the <c>async void</c> command
    /// pipeline into WPF's DispatcherUnhandledException handler,
    /// leaving the VM with stale bindable state from the prior load.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            ErrorMessage = null;
            // v3.51.0 MINOR T4 (UI 接入): filter 现在同时列出 .asc + .blf。
            // 后端 ReplayService.LoadAsync 在 T3 已分派 .blf → BlfParser / .asc → AscParser,
            // 所以同一 dialog 同时覆盖两种格式。WPF OpenFileDialog 的 "|" 语法允许多扩展名,
            // 用分号分隔以避免每个扩展独立条目降低 UX。
            var path = _fileDialog.ShowOpenDialog(
                filter: "Replay files (*.asc;*.blf)|*.asc;*.blf|ASC files (*.asc)|*.asc|BLF files (*.blf)|*.blf");
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
        catch (OperationCanceledException)
        {
            // v3.8.4 PATCH H1: same teardown as ReplayException, but no
            // ErrorMessage (cancel is not a user-hostile failure).
            // Symmetric to the OpenSessionAsync failure branch (v3.8.3
            // H1) — VM-side bindable surface goes back to a clean
            // "not loaded" state so the transport bar greys out.
            IsLoaded = false;
            LoadedFilePath = null;
            TotalDuration = 0.0;
            ScrubberMaxValue = 0.0;
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
        // v3.8.4 PATCH H2: extend the teardown to also reset the
        // service-side frame buffer. v3.8.3 H1 only cleared VM-side
        // state; in a multi-source partial-success bundle (source #1
        // loads fine, source #2 throws), the service's _frames list
        // is left populated with source #1's data even though the VM
        // reports IsLoaded=false. Calling _service.Reset() drops the
        // frames + stops the timeline so the service's authoritative
        // state matches the VM's reported state.
        if (missing.Count > 0)
        {
            _service.Reset();
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
            ErrorMessage = $"{missing.Count} replay file(s) (.asc/.blf) could not be located. Use Replay → Open... to reload the source.";
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
}