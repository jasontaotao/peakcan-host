using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ReplayViewModel
{
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
    /// v3.11.0 MINOR T2 (H7): the scalar envelope (Version / Schema /
    /// SavedAt / AppVersion / DbcPath / GlobalCanIdFilter) + content-hash
    /// computation now lives in <see cref="TraceSessionSnapshotBuilder"/>.
    /// This method is the thin sync shim over <see cref="BuildSnapshotAsync"/>;
    /// new callers should prefer the async form.
    /// </para>
    /// </summary>
    public TraceSessionBundleDto BuildSnapshot() =>
        BuildSnapshotAsync().GetAwaiter().GetResult();

    /// <summary>
    /// v3.11.0 MINOR T2 (H7): async BuildSnapshot entry point. Builds
    /// the scaffold from the VM's bindable state, asks the shared
    /// builder for the scalar envelope + content hash, then appends
    /// the Replay-specific <see cref="TraceSessionBundleDto.Sources"/>
    /// and <see cref="TraceSessionBundleDto.Playback"/> envelopes.
    /// <see cref="TraceSessionBundleDto.Viewports"/> stays empty (Replay
    /// has no chart subplots). CT propagates to the hasher for the
    /// SHA-256 round-trip.
    /// </summary>
    public async Task<TraceSessionBundleDto> BuildSnapshotAsync(CancellationToken ct = default)
    {
        // Scaffold fields mirror the VM's bindable state at Build time.
        // Start/End default to 0.0 when the range filter is unbounded
        // (matches the legacy "no filter" semantics).
        var scaffold = new TraceSessionSnapshotBuilder.Scaffold(
            LoadedFilePath: LoadedFilePath,
            CurrentTimestamp: CurrentTimestamp,
            Speed: Speed,
            Loop: Loop,
            StartTimestamp: StartTimestamp ?? 0.0,
            EndTimestamp: EndTimestamp ?? 0.0,
            CanIdFilterText: CanIdFilterText ?? "",
            DbcPath: "");
        var dto = await _builder.BuildAsync(scaffold, ct).ConfigureAwait(true);

        var displayName = string.IsNullOrEmpty(LoadedFilePath)
            ? ""
            : Path.GetFileNameWithoutExtension(LoadedFilePath);
        var sourceId = Guid.NewGuid().ToString("N");
        // Builder pre-populated Sources[0] with the hash. We rebuild the
        // single entry to add the per-source display metadata + GUID id;
        // carry the builder's hash over so bundle hash-based relocation
        // (v3.6.4 PATCH) keeps working on reload.
        var builderHash = dto.Sources.Count > 0 ? dto.Sources[0].ContentHash : "";
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
                ContentHash = builderHash,
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
            // v3.51.0 MINOR T4 follow-up: bundle sources can now be .blf
            // (not just .asc), so the wording is format-agnostic and
            // the open-action pointer reflects the Replay tab's button
            // label (not the legacy File menu which no longer exists).
            ErrorMessage = $"{missing.Count} replay file(s) (.asc/.blf) could not be located. Use Replay → Open... to reload the source.";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle source missing or unreadable: {Path}")]
    private static partial void LogSourceMissing(ILogger logger, string? path, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle source relocated via content hash: {OldPath} -> {NewPath}")]
    private static partial void LogRelocated(ILogger logger, string? oldPath, string? newPath);
}