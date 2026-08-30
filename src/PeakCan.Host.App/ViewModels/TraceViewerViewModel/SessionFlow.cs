using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Session save (v3.5.0 MINOR + later patches). Session load +
    // snapshot restore moved to ITraceSessionService (会话状态剥离 Task 1/3);
    // v3.x Task 4: 原 OpenSessionAsync 薄转发已删除——TraceSessionAutoSaver
    // 已改直连 service，VM 不再保留会话恢复入口。
    //
    // Cross-flow references (stay as plain calls via partial-class visibility):
    //   - BuildSnapshotAsync → _builder.BuildAsync (TraceSessionSnapshotBuilder)
    //                          → _hasher.ComputeAsync (ITraceContentHasher)

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
    /// v3.5.0 MINOR: collect the current session state into a
    /// <see cref="TraceSessionBundleDto"/>. Pure — no I/O, no side
    /// effects. Path-reference only for .asc recordings; playback
    /// scalars are written with window-level defaults (播放已废除) and
    /// the DBC path is recorded (the DBC service is not re-loaded — the
    /// caller reloads it as part of session restore once the sources
    /// are loaded).
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
            CurrentTimestamp: 0.0,   // 播放已废除 → 窗口级默认
            Speed: 1.0,              // 播放已废除 → 窗口级默认
            Loop: false,             // 播放已废除 → 窗口级默认
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
                StrokeStyle = src.StrokeStyle?.ToString() ?? "",
                CanIdFilter = src.CanIdFilter ?? "",
                ContentHash = hash,
            });
        }
        dto.Playback = new BundlePlaybackDto
        {
            MasterSourceId = MasterSourceId ?? "",
            Loop = false, Speed = 1.0, ScrubberValue = 0.0,
            StartTimestamp = null, EndTimestamp = null,
        };
        dto.Viewports = new List<BundleViewportDto>();
        // v12 Step 7: persist watch list + groups.
        dto.WatchedSignals = WatchedSignals
            .Where(r => !r.IsPlaceholder)
            .Select(r => new BundleWatchedSignalDto
            {
                CanIdHex = r.CanIdHex,
                MessageName = r.MessageName,
                SignalName = r.SignalName,
                Unit = r.Unit,
                SourceId = r.SourceId,
                Alias = r.Alias,
            }).ToList();
        dto.Groups = SignalGroups
            .Select(g => new BundleGroupDto
            {
                Id = g.Id,
                Name = g.Name,
                Notes = g.Notes,
                SignalKeys = g.SignalKeys.ToList(),
            }).ToList();
        return dto;
    }

    /// <summary>
    /// v3.x (会话状态剥离 Task 5 final, Important #2): service 的 OpenSessionAsync
    /// 恢复完 watch 列表 + 分组后触发 <see cref="ITraceSessionService.SessionRestored"/>。
    /// VM 的 WatchedSignals 与 service 是同一集合，但恢复发生在最后一次 SourcesChanged
    /// 驱动的 RefreshFrameCounts 之后——这里补刷 FrameCount 与锚点值，否则新恢复的行
    /// 显示空值/旧值（原 ApplySnapshotAsync 结尾的 RefreshAtAnchor 模式）。事件在
    /// UI 线程触发（service 全程 ConfigureAwait(true)），可直接触碰绑定集合。
    /// </summary>
    private void OnSessionRestored()
    {
        RebindMasterServiceIfChanged();
        // Task 12 review fix (Important): OpenSessionAsync loads traces via
        // _registry.LoadAsync directly (TraceSessionService.cs:173), bypassing
        // AddTraceAsync — without this the L2 panel stays silently empty after
        // File ▸ Open Session. Event-driven (never runs during construction),
        // so the ctor-time ReassembledMessages contract is unaffected.
        RebuildJ1939ViewsCommand.Execute(null);
        if (_dbcService.Current is not null) RefreshFrameCounts();
        if (!double.IsNaN(_anchorTimestampSeconds))
            RefreshAtAnchor(_anchorTimestampSeconds);
        if (!double.IsNaN(_blueAnchorTimestampSeconds))
            RefreshAtAnchorBlue(_blueAnchorTimestampSeconds);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "BuildSnapshot: hashing failed for {Path}; bundle saved without contentHash")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string path);
}
