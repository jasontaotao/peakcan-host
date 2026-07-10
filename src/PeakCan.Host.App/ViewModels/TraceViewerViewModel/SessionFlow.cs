using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OxyPlot;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow E: Session save/load + bundled restore (v3.5.0 MINOR + later patches).
    // Methods moved verbatim from TraceViewerViewModel.cs.
    //
    // Cross-flow references (stay as plain calls via partial-class visibility):
    //   - ApplySnapshotAsync → RebuildSignalsCore (Flow C, in SignalFlow.cs)
    //                          → ChartViewModel.ApplyViewports (TraceChartViewModel member)
    //                          → _registry.LoadAsync / UnloadAsync (Flow A dependency)
    //                          → LogRelocated / LogSourceMissing / LogBundleDbcLoadFailedInline
    //                            (Flow A log helpers — partial-class visible)
    //   - BuildSnapshotAsync → _builder.BuildAsync (TraceSessionSnapshotBuilder)
    //                          → _hasher.ComputeAsync (ITraceContentHasher)
    //   - ApplySnapshotAsync → _dbcService.LoadAsync (DbcService)

    /// <summary>
    /// v3.5.0 MINOR: save the current Trace Viewer session to a
    /// <c>.tmtrace</c> bundle. <paramref name="path"/> is supplied by
    /// the View's <c>SaveFileDialog</c>; the command itself does NOT
    /// pop a dialog (testability — the View handles the file dialog
    /// to keep WPF dependency out of the VM).
    /// </summary>
    [RelayCommand]
    public async Task SaveSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var snapshot = BuildSnapshot();
        await Task.Run(() => _sessionLibrary.Save(snapshot, path)).ConfigureAwait(true);
    }

    /// <summary>
    /// v3.5.0 MINOR: load a Trace Viewer session from a <c>.tmtrace</c>
    /// bundle. The caller (View) handles the open-file dialog and the
    /// missing-ascs MessageBox UX — the VM returns the list of paths
    /// that could not be resolved (e.g. an .asc that was moved/deleted
    /// since the bundle was saved) so the View can surface them.
    /// Restores playback to a paused/stopped cursor — never auto-resumes.
    /// </summary>
    /// <returns>List of source .asc paths that did NOT resolve on load.
    /// Empty when the bundle had no sources or when every source
    /// resolved cleanly.</returns>
    public async Task<IReadOnlyList<string>> OpenSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        var dto = await Task.Run(() => _sessionLibrary.Load(path)).ConfigureAwait(true);
        if (dto is null) return Array.Empty<string>();
        return await ApplySnapshotAsync(dto).ConfigureAwait(true);
    }

    /// <summary>
    /// v3.5.0 MINOR: collect the current session state into a
    /// <see cref="TraceSessionBundleDto"/>. Pure — no I/O, no side
    /// effects. Path-reference only for .asc recordings; playback state
    /// is captured verbatim (master, loop, speed, scrubber) and the
    /// DBC path is recorded (the DBC service is not re-loaded — the
    /// caller will reload it as part of <see cref="ApplySnapshotAsync"/>
    /// once the sources are loaded).
    /// <para>
    /// v3.6.0 MINOR T2: access changed from <c>private</c> to
    /// <c>public</c> so <see cref="TraceSessionAutoSaver"/> can snapshot
    /// the live VM during <c>App.OnExit</c>. Behavior unchanged.
    /// </para>
    /// <para>
    /// v3.11.0 MINOR T2 (H7): the scalar envelope (Version / Schema /
    /// SavedAt / AppVersion / DbcPath / GlobalCanIdFilter) now lives in
    /// <see cref="TraceSessionSnapshotBuilder"/>. This method is the
    /// thin sync shim over <see cref="BuildSnapshotAsync"/>; new
    /// callers should prefer the async form. Per-source iteration +
    /// per-source hashing still lives here (N sources + per-source
    /// color / stroke style / filter).
    /// </para>
    /// </summary>
    public TraceSessionBundleDto BuildSnapshot() =>
        BuildSnapshotAsync().GetAwaiter().GetResult();

    /// <summary>
    /// v3.11.0 MINOR T2 (H7): async BuildSnapshot entry point. Same
    /// shape as <see cref="BuildSnapshot"/> but awaits the shared
    /// builder's scalar envelope assembly. CT propagates to each
    /// per-source hasher call.
    /// </summary>
    public async Task<TraceSessionBundleDto> BuildSnapshotAsync(CancellationToken ct = default)
    {
        var scaffold = new TraceSessionSnapshotBuilder.Scaffold(
            LoadedFilePath: null,    // Trace iterates N sources — the builder's single-source path is unused
            CurrentTimestamp: ScrubberValue,
            Speed: Speed,
            Loop: Loop,
            StartTimestamp: 0.0,
            EndTimestamp: 0.0,
            CanIdFilterText: CanIdFilter ?? "",
            DbcPath: LoadedDbcPath ?? "");
        var dto = await _builder.BuildAsync(scaffold, ct).ConfigureAwait(true);

        // Per-source assembly stays on the VM: N sources, per-source
        // color + stroke style + filter, plus N per-source hashes
        // (the builder's single-source pre-population is overwritten).
        dto.Sources = new List<BundleSourceDto>(Sources.Count);
        foreach (var src in Sources)
        {
            // v3.6.4 PATCH: populate contentHash when the source's
            // .asc still exists on disk so the bundle can later be
            // relocated via the SHA-256 lookup.
            var hash = "";
            if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
            {
                try
                {
                    hash = await _hasher.ComputeAsync(src.Path, ct).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Hashing failed (locked file / ACL). Skip — the
                    // bundle still saves with contentHash="" and the
                    // path-only resolution covers it on reload.
                    LogHashFailed(_logger, ex, src.Path);
                    hash = "";
                }
            }
            dto.Sources.Add(new BundleSourceDto
            {
                SourceId = src.SourceId,
                DisplayName = src.DisplayName,
                Path = src.Path,
                ColorA = src.Color.A,
                ColorR = src.Color.R,
                ColorG = src.Color.G,
                ColorB = src.Color.B,
                StrokeStyle = src.StrokeStyle.ToString(),
                CanIdFilter = src.CanIdFilter ?? "",
                ContentHash = hash,
            });
        }
        dto.Playback = new BundlePlaybackDto
        {
            MasterSourceId = MasterSourceId ?? "",
            Loop = Loop,
            Speed = Speed,
            ScrubberValue = ScrubberValue,
            StartTimestamp = null,
            EndTimestamp = null,
        };
        dto.Viewports = new List<BundleViewportDto>(ChartViewModel.CaptureViewports());
        return dto;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "BuildSnapshot: hashing failed for {Path}; bundle saved without contentHash")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string path);

    /// <summary>
    /// v3.5.0 MINOR: restore a saved session. Loads each .asc via the
    /// registry, applies playback state (always to a paused cursor —
    /// never auto-resumes), then restores chart viewports AFTER
    /// <see cref="RebuildSignalsCore"/> populates the Series collection
    /// (otherwise the per-axis writes would land on stale or empty
    /// PlotModels and <see cref="TraceChartViewModel.SyncYAxes"/> would
    /// overwrite them).
    /// </summary>
    private async Task<IReadOnlyList<string>> ApplySnapshotAsync(TraceSessionBundleDto dto)
    {
        var missing = new List<string>();
        // Unload any currently-loaded sources so the session is exactly
        // what the bundle describes. UnloadAsync is async but the inner
        // work is synchronous; we await to keep ordering deterministic.
        foreach (var src in Sources.ToList())
        {
            await _registry.UnloadAsync(src.SourceId).ConfigureAwait(true);
        }
        // Map sourceId → DisplayName so we can re-stamp after load.
        var nameBySourceId = dto.Sources.ToDictionary(s => s.SourceId, s => s.DisplayName, StringComparer.Ordinal);
        var pathBySourceId = dto.Sources.ToDictionary(s => s.SourceId, s => s.Path, StringComparer.Ordinal);
        // 1. Reload the .asc files via the registry. Missing → recorded
        //    in the returned list; do NOT throw (user-friendly).
        foreach (var bs in dto.Sources)
        {
            // v3.6.4 PATCH: when the recorded path is missing AND the
            // bundle carries a contentHash, ask the locator for a
            // relocated copy before giving up. The relocated path is
            // used for the registry load; if the locator also fails,
            // we fall through to the existing missing-path reporting.
            var loadPath = bs.Path;
            if (!string.IsNullOrEmpty(bs.Path) &&
                !File.Exists(bs.Path) &&
                !string.IsNullOrEmpty(bs.ContentHash))
            {
                var relocated = await _locator.LocateAsync(bs.ContentHash).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(relocated) && File.Exists(relocated))
                {
                    LogRelocated(_logger, bs.Path, relocated);
                    loadPath = relocated;
                }
            }
            try
            {
                var loaded = await _registry.LoadAsync(loadPath).ConfigureAwait(true);
                // v3.6.0 MINOR T1.B: restore DisplayName and color from
                // the bundle, replacing the v3.5.0 "path-reference only"
                // comment. The registry's LoadAsync stamps a default
                // DisplayName (filename) and palette color; both are
                // overwritten when the bundle supplies values. For
                // bundle entries where color was left at default ARGB =
                // (0,0,0,0), the property set is skipped so the
                // registry's palette color survives (forward-compat with
                // hand-edited v1 bundles that pre-date color capture).
                loaded.CanIdFilter = bs.CanIdFilter;
                var filenameOnly = Path.GetFileNameWithoutExtension(bs.Path);
                if (!string.IsNullOrEmpty(bs.DisplayName) &&
                    bs.DisplayName != filenameOnly)
                {
                    loaded.DisplayName = bs.DisplayName;
                }
                if (!(bs.ColorA == 0 && bs.ColorR == 0 &&
                      bs.ColorG == 0 && bs.ColorB == 0))
                {
                    loaded.Color = OxyColor.FromArgb(
                        bs.ColorA, bs.ColorR, bs.ColorG, bs.ColorB);
                }
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
        // 2. Apply DBC path if present. Best-effort — missing DBC is
        //    acceptable (user can reload manually).
        if (!string.IsNullOrEmpty(dto.DbcPath) && File.Exists(dto.DbcPath))
        {
            try { await _dbcService.LoadAsync(dto.DbcPath).ConfigureAwait(true); }
            catch (FileNotFoundException) { /* bundle references a deleted DBC — acceptable */ }
            catch (Exception ex)
            {
                // v3.9.2 PATCH L1: was a bare catch{ } swallowing all failures.
                // Log the DBC load failure so the operator can diagnose a
                // malformed-vendor-DBC without losing visibility. StatusMessage
                // surfaces it on the toolbar; the source still loads (the
                // bundle is path-reference only, so a missing/bad DBC is
                // not fatal — the user can reload manually).
                // v3.13.0 PATCH F3: renamed helper (was LogBundleDbcLoadFailed).
                // LoadDbcAsync's deletion made the old name misleading; this
                // arm is now the only caller.
                LogBundleDbcLoadFailedInline(_logger, dto.DbcPath, ex);
                StatusMessage = $"DBC load failed: {ex.Message}";
            }
            LoadedDbcPath = dto.DbcPath;
        }
        else
        {
            LoadedDbcPath = dto.DbcPath ?? "";
        }
        // 3. Apply global filter + playback transport. Always to a
        //    paused cursor — never auto-resume on app restart.
        CanIdFilter = dto.GlobalCanIdFilter ?? "";
        Loop = dto.Playback?.Loop ?? false;
        Speed = dto.Playback?.Speed ?? 1.0;
        ScrubberValue = 0.0;
        if (dto.Playback is { } pb && !string.IsNullOrEmpty(pb.MasterSourceId))
        {
            // The new SourceId from registry.LoadAsync != bundle's pre-recorded
            // id. Map via display name — same alpha-order as the registry
            // adds them, and the bundle's order matches.
            var newMaster = Sources.FirstOrDefault(s =>
                string.Equals(s.DisplayName, nameBySourceId.GetValueOrDefault(pb.MasterSourceId, ""), StringComparison.Ordinal));
            if (newMaster is not null)
            {
                MasterSourceId = newMaster.SourceId;
                _masterService = _registry.GetService(newMaster.SourceId);
                TotalDuration = _masterService?.TotalDuration ?? 0.0;
                ChartViewModel.SetTotalDuration(TotalDuration);
                // Seek to saved scrubber position (paused).
                if (_masterService is not null && pb.ScrubberValue > 0)
                {
                    _masterService.Seek(pb.ScrubberValue);
                    ScrubberValue = pb.ScrubberValue;
                }
            }
        }
        // 4. Rebuild signals + chart with the new source set, then apply
        //    viewports AFTER SyncYAxes has run so the X-axis writes stick.
        RebuildSignalsCore();
        // v3.5.1 PATCH (review M2): explicit assignment removes the
        // implicit dependency on _registry.LoadAsync firing
        // OnRegistrySourcesChanged synchronously inside ApplySnapshotAsync.
        // If the registry were ever to dispatch SourcesChanged
        // asynchronously, the property would still be correct here.
        LoadedTracePath = Sources.Count > 0 ? Sources[0].Path : "";
        ChartViewModel.ApplyViewports(dto.Viewports);
        return missing;
    }
}