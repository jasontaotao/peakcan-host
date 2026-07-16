using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow A: Source management (registry add/remove + master swap + DBC load).
    // Methods moved verbatim from TraceViewerViewModel.cs.
    //
    // Cross-flow callers (all stay as plain calls because partial-class
    // visibility makes private members visible across files):
    //   - SetMaster (Flow A) → DetachAllServiceHandlers / AttachAllServiceHandlers (Flow F)
    //   - OnRegistrySourcesChanged (Flow A) → RefreshFrameCounts (Flow C)
    //                            → RemoveOrphanChartSeries (Flow A, intra-flow)
    //                            → AttachAllServiceHandlers / DetachAllServiceHandlers (Flow F)
    //                            → RebindMasterFromRegistry (stays in main file)
    //   - OnDbcLoaded (Flow A) → RebuildSignalsCore (Flow C)
    //   - AddTraceAsync (Flow A) → _registry.LoadAsync (registry, Flow A own dependency)

    /// <summary>
    /// v3.2.0 MINOR: append a new trace to the session. v3.9.1 PATCH
    /// Bug #2: now absorbs failures into bindable state
    /// (<see cref="ErrorMessage"/> + <see cref="StatusMessage"/>) instead
    /// of rethrowing into the View's <c>async void</c> click handler.
    /// Mirrors <see cref="ReplayViewModel.OpenAsync"/>'s try/catch shape:
    /// <list type="bullet">
    ///   <item><see cref="OperationCanceledException"/> → swallowed,
    ///     <c>StatusMessage = "Load cancelled"</c>, no ErrorMessage.</item>
    ///   <item><see cref="ReplayException"/> → <c>ErrorMessage = ex.Message</c>,
    ///     <c>StatusMessage = "Load failed"</c>.</item>
    /// </list>
    /// <see cref="IsLoading"/> flips true → false in <c>finally</c> so the
    /// toolbar button re-enables regardless of success or failure path.
    /// <para>
    /// v3.9.1 PATCH: <c>CanExecute = nameof(CanAddTrace)</c> so the
    /// generated <c>AddTraceCommand</c> respects <see cref="IsLoading"/> —
    /// the toolbar button greys out during a load.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddTrace))]
    public async Task AddTraceAsync()
    {
        // v3.11.4 PATCH: the file dialog moved from the View (CommandParameter=""
        // hack) into the VM via the already-injected IFileDialogService. The
        // empty-string path that triggered the "Unexpected error: path must be
        // non-empty" user-facing regression can no longer reach the registry —
        // the dialog either returns a real path or null (cancellation).
        var dialog = _fileDialog;
        if (dialog is null)
        {
            // Defensive fallback when IFileDialogService wasn't injected (e.g.,
            // unit-test fixtures that build the VM without DI). Surface as an
            // error rather than crashing — the user can still type the path
            // manually via the .tmtrace session Save/Open flow.
            ErrorMessage = "File dialog service unavailable. Use File ▸ Open Session... instead.";
            StatusMessage = "Add trace unavailable";
            LogLoadFailed(_logger, new InvalidOperationException("IFileDialogService not injected"), "(no dialog)");
            return;
        }

        string? path;
        try
        {
            // v3.51.0 MINOR T5: Trace Viewer → "Add trace..." 按钮 filter
            // 现在同时列 .asc + .blf (3 段：组合默认 / ASC 单独 / BLF 单独
            // / All files 回退)。Sister pattern of ReplayViewModel.
            // Loader.partial.cs:96 (T4 commit 3de61db).
            path = dialog.ShowOpenDialog(
                "Trace files (*.asc;*.blf)|*.asc;*.blf|ASC files (*.asc)|*.asc|BLF files (*.blf)|*.blf|All files|*.*");
        }
        catch (Exception ex)
        {
            // The WPF OpenFileDialog throws if no Application is running or if
            // the dispatcher is shutting down. Surface as a user-visible error
            // and stay silent otherwise.
            LogLoadFailed(_logger, ex, "(dialog)");
            ErrorMessage = $"File dialog failed: {ex.Message}";
            StatusMessage = "Add trace failed";
            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            // Cancellation (dialog returned null) or pathological empty-string
            // return (impossible from production OpenFileDialog but defended
            // for test fakes). Silent no-op per v3.11.4 PATCH contract.
            return;
        }

        try
        {
            ErrorMessage = null;
            IsLoading = true;
            var name = System.IO.Path.GetFileName(path);
            StatusMessage = $"Loading {name}…";
            await _registry.LoadAsync(path).ConfigureAwait(true);
            StatusMessage = $"Loaded {name}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Load cancelled";
        }
        catch (ReplayException ex)
        {
            LogLoadFailed(_logger, ex, path);
            ErrorMessage = ex.Message;
            StatusMessage = "Load failed";
        }
        catch (Exception ex)
        {
            // v3.9.2 PATCH H10: defensive fallback catch. AddTraceAsync is
            // invoked through an async-void command, so any exception that
            // escapes the typed arms above would propagate to WPF
            // DispatcherUnhandledException, where App.xaml.cs:332 deliberately
            // does NOT mark Handled — resulting in process termination.
            // v3.11.4 PATCH: this catch can no longer be reached for the
            // empty-path case (dialog validates before the path is forwarded),
            // but the defensive arm stays for registry hook throws (SourcesChanged
            // listener, ApplyAutoSnapshotAsync, etc.).
            // v3.13.0 PATCH F1: include ex.GetType().Name + first stack
            // frame so the user sees the throw type AND the originating
            // call site inline (e.g. "NullReferenceException: ... |
            // at Foo.Bar() in C:\src\X.cs:line 42") without opening
            // the Serilog file. ex.Message alone is often too generic
            // to diagnose (e.g. NRE's "Object reference not set to an
            // instance of an object." gives no class/method hint).
            // Full stack trace is still captured in the log.
            var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            LogLoadFailed(_logger, ex, path);
            ErrorMessage = $"Unexpected error ({ex.GetType().Name}): {ex.Message} | {firstFrame}";
            StatusMessage = "Load failed";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// v3.2.0 MINOR: remove a source from the session. Invoked by the
    /// per-source "✕" button in the legend strip.
    /// </summary>
    [RelayCommand]
    public async Task RemoveTraceAsync(string sourceId)
    {
        await _registry.UnloadAsync(sourceId).ConfigureAwait(true);
    }

    /// <summary>
    /// v3.9.1 PATCH Bug #2: <c>CanExecute</c> predicate for
    /// <see cref="AddTraceCommand"/>. Disables the toolbar button while a
    /// load is in flight.
    /// </summary>
    // v3.11.4 PATCH: AddTraceCommand is parameterless now (the VM owns the
    // file-dialog flow). The CanExecute predicate must NOT take a path arg —
    // any CanExecute(string.Empty) call would silently disable the command,
    // which was the v3.9.1 PATCH B2 root cause.
    private bool CanAddTrace() => !IsLoading;

    /// <summary>
    /// v3.3.0 MINOR: switch the master source mid-session. Stops playback,
    /// swaps the master, restarts if was playing. If the new sourceId is
    /// not in <see cref="_allServices"/> the call is a no-op. After the
    /// swap the previous master's <c>PlaybackEnded</c> handler is detached
    /// and the new master's is attached (via the standard attach/detach
    /// lifecycle) so the loop rewind anchor follows the active master.
    /// </summary>
    [RelayCommand]
    public void SetMaster(string sourceId)
    {
        if (sourceId == MasterSourceId) return;
        if (!_allServices.TryGetValue(sourceId, out var newMaster)) return;
        var wasPlaying = _masterService?.State == ReplayState.Playing;
        Stop();   // resets all services to t=0
        MasterSourceId = sourceId;
        _masterService = newMaster;
        TotalDuration = _masterService.TotalDuration;
        ChartViewModel.SetTotalDuration(TotalDuration);
        // Reattach event handlers — the previous master had FrameEmitted +
        // PlaybackEnded subscribed; the new master needs the same hooks.
        DetachAllServiceHandlers();
        AttachAllServiceHandlers();
        PropagateLoopToAllServices();
        PropagateSpeedToAllServices();
        // Master swap can change which signal rows have data (different
        // frame set); rebuild off-thread to avoid blocking the UI.
        _ = RebuildSignalsAsync();
        if (wasPlaying) Play();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load trace: {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception ex, string path);

    // v3.5.0 MINOR: bundle-load could not resolve one of the recorded .asc
    // paths (file moved, deleted, or on a currently-unmounted drive). The
    // caller (View) surfaces the missing paths via a MessageBox so the user
    // can decide whether to remap or proceed without.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle source missing or unreadable: {Path}")]
    private static partial void LogSourceMissing(ILogger logger, string path, Exception ex);

    // v3.6.4 PATCH: hash-based relocation recovered a missing .asc.
    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle source relocated via content hash: {OldPath} -> {NewPath}")]
    private static partial void LogRelocated(ILogger logger, string oldPath, string newPath);

    // v3.9.2 PATCH L1: source-gen'd log helper for the bundle DBC load
    // fallback catch (was bare catch { } before).
    // v3.13.0 PATCH F3: renamed from LogBundleDbcLoadFailed → LogBundleDbcLoadFailedInline
    // (signature unchanged). The LoadDbcAsync public method was removed
    // (toolbar "Load DBC…" button had no UI feedback because LoadedDbcPath
    // was never bound in TraceViewerView.xaml). The bundle-load catch arm
    // at the former line 678 is the LAST remaining caller; DbcView tab is
    // now the single entry point for ad-hoc DBC loading.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle DBC load failed for {Path}")]
    private static partial void LogBundleDbcLoadFailedInline(ILogger logger, string path, Exception ex);

    /// <summary>
    /// v3.2.0 MINOR: react to <see cref="ITraceSessionRegistry.SourcesChanged"/>
    /// — re-pin master to first source, update TotalDuration + ChartViewModel
    /// duration, refresh LoadedTracePath (legacy binding). v3.3.0 MINOR:
    /// attach FrameEmitted + master PlaybackEnded handlers and propagate
    /// Loop/Speed to every newly registered service.
    /// <para>
    /// v3.14.1 PATCH: also call <see cref="RebuildSignalsCore"/> at the
    /// end so loading a new .asc via <c>AddTraceAsync</c> re-decodes the
    /// (already-loaded) DBC messages against the new source's frames.
    /// Pre-fix this method updated the service dictionary + master but
    /// never rebuilt signals — the user had to reload the DBC to refresh.
    /// </para>
    /// </summary>
    private void OnRegistrySourcesChanged()
    {
        DetachAllServiceHandlers();
        DetachAllSourcePropertyHandlers();   // v3.4.3 PATCH
        _allServices.Clear();
        foreach (var src in _registry.Sources)
        {
            var svc = _registry.GetService(src.SourceId);
            if (svc is null) continue;
            _allServices[src.SourceId] = svc;
            // v3.4.3 PATCH: subscribe to per-source filter changes (manual
            // INPC on TraceSource.CanIdFilter). Detach happens first above;
            // re-attaching here is safe even if the registry contains the
            // same instance across consecutive SourcesChanged events.
            src.PropertyChanged += OnAnySourcePropertyChanged;
            // Multi-trace sync mode: ignore per-source playback range
            // (each source's playable range = full [0, TotalDuration]).
            if (_registry.Sources.Count > 1)
            {
                svc.StartTimestamp = null;
                svc.EndTimestamp = null;
            }
        }
        RebindMasterFromRegistry();
        AttachAllServiceHandlers();
        PropagateLoopToAllServices();
        PropagateSpeedToAllServices();
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(HasSources));
        LoadedTracePath = Sources.Count > 0 ? Sources[0].Path : "";
        TotalDuration = _masterService?.TotalDuration ?? 0.0;
        ChartViewModel.SetTotalDuration(TotalDuration);
        // v3.14.3 PATCH: do NOT clear ChartViewModel.Series — preserve
        // user opt-ins (rows they previously checked in the signal
        // table). Orphan chart series (sources unloaded) are removed
        // below by RemoveOrphanChartSeries().
        // v3.14.3 PATCH: do NOT call RebuildSignalsCore (which clears
        // Signals). Use RefreshFrameCounts instead — it updates per-row
        // FrameCount + LatestValue in place without rebuilding the row
        // catalog. The DBC has not changed here; only data sources.
        if (_dbcService.Current is not null)
        {
            RefreshFrameCounts();
            RemoveOrphanChartSeries();
            ChartViewModel.SyncYAxes();
            ChartViewModel.SyncXAxis(0, _masterService?.TotalDuration ?? 0);
        }
    }

    /// <summary>
    /// v3.14.3 PATCH: walk <see cref="ChartViewModel.Series"/> and
    /// remove any chart row whose <see cref="TraceChartSeries.SourceId"/>
    /// is no longer present in the registry. Called from
    /// <see cref="OnRegistrySourcesChanged"/> after an unload.
    /// </summary>
    private void RemoveOrphanChartSeries()
    {
        var liveSources = new HashSet<string>(_registry.Sources.Select(s => s.SourceId));
        var snapshot = ChartViewModel.Series
            .Where(s => !liveSources.Contains(s.SourceId))
            .ToList();
        foreach (var orphan in snapshot)
            ChartViewModel.RemoveSeries(orphan);
    }

    // v3.13.2 PATCH F5: rebuild Signals + chart subplots when a DBC is
    // loaded via the DbcView tab. v3.15.0 MINOR: also update
    // LoadedDbcPath so the XAML top bar reflects the currently loaded
    // DBC file (DbcDocument.SourcePath was added in v3.15.0).
    private void OnDbcLoaded(DbcDocument doc)
    {
        LoadedDbcPath = doc.SourcePath ?? "";
        RebuildSignalsCore();
        // v3.50.5 PATCH: re-bind Dbc on every existing WatchedSignals row
        // so the .Text computed properties resolve VAL_ table entries
        // against the newly-loaded DBC. The Dbc setter triggers
        // PropertyChanged for LatestText/BlueText/DeltaText (INPC fan-out).
        // Signal references remain cached in _signalByKey (sister of v3.50.0
        // MINOR); a stale Signal after DBC reload is a known pre-existing
        // issue out of scope for v3.50.5.
        foreach (var row in WatchedSignals)
        {
            row.Dbc = doc;
        }
    }
}