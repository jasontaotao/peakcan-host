using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Replay;
using PeakCan.HIL.Core.Services;
using ScottPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.x (会话状态剥离 Task 1): <see cref="ITraceSessionService"/> 的默认实现。
/// 会话级状态的唯一归属——4 组状态 + OpenSession / BuildSnapshot 从
/// TraceViewerViewModel.SessionFlow 迁移至此。窗口级字段在快照中替换为默认值
/// （CurrentTimestamp=0 / Speed=1 / Loop=false / Viewports=空），窗口状态仍由
/// TraceViewerViewModel 负责。
/// <para>
/// 迁移来源（现状基准）：<c>TraceViewerViewModel/SessionFlow.cs</c> 的
/// <c>BuildSnapshotAsync</c>（91-174）与 <c>ApplySnapshotAsync</c>（188-351）。
/// </para>
/// </summary>
public sealed partial class TraceSessionService : ObservableObject, ITraceSessionService
{
    private readonly ITraceSessionRegistry _registry;
    private readonly TraceSessionLibrary _library;
    private readonly DbcService _dbcService;
    private readonly IAscLocator _locator;
    private readonly IAscContentHasher _hasher;
    private readonly TraceSessionSnapshotBuilder _builder;
    private readonly ILogger<TraceSessionService> _logger;

    /// <summary>watch 列表行（get-only；占位行由 BuildSnapshot 过滤）。</summary>
    public ObservableCollection<WatchedSignalRow> WatchedSignals { get; } = new();

    /// <summary>信号分组（get-only）。</summary>
    public ObservableCollection<WatchedSignalGroup> SignalGroups { get; } = new();

    /// <summary>master source 的 SourceId（INPC）。</summary>
    [ObservableProperty]
    private string? _masterSourceId;

    /// <summary>全局 CAN-ID 过滤器文本（INPC；空串 = 不过滤）。</summary>
    [ObservableProperty]
    private string _globalCanIdFilter = "";

    /// <summary>
    /// v3.x (会话状态剥离 Task 5 final, Important #2): OpenSessionAsync
    /// 恢复完 watch 列表 + 分组后触发。在调用方 SynchronizationContext 上
    /// 触发（OpenSessionAsync 全程 ConfigureAwait(true) 保证），供开着的
    /// VM 补刷 FrameCount / 锚点。
    /// </summary>
    public event Action? SessionRestored;

    /// <inheritdoc />
    public bool HasContent => _registry.Sources.Count > 0;

    public TraceSessionService(
        ITraceSessionRegistry registry,
        TraceSessionLibrary library,
        DbcService dbcService,
        IAscLocator locator,
        IAscContentHasher hasher,
        TraceSessionSnapshotBuilder builder,
        ILogger<TraceSessionService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public TraceSessionBundleDto BuildSnapshot()
    {
        var scaffold = new TraceSessionSnapshotBuilder.Scaffold(
            LoadedFilePath: null,
            CurrentTimestamp: 0.0,     // 窗口级 → 默认
            Speed: 1.0,                // 窗口级 → 默认
            Loop: false,               // 窗口级 → 默认
            StartTimestamp: 0.0,
            EndTimestamp: 0.0,
            CanIdFilterText: GlobalCanIdFilter,
            DbcPath: _dbcService.Current?.SourcePath ?? "");
        var dto = _builder.BuildAsync(scaffold, CancellationToken.None).GetAwaiter().GetResult();

        dto.Sources = new List<BundleSourceDto>(_registry.Sources.Count);
        foreach (var src in _registry.Sources)
        {
            // v3.6.4 PATCH: 文件仍存在时才填充 contentHash，供 reload 时按 SHA-256 重定位。
            var hash = "";
            if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
            {
                try { hash = _hasher.ComputeAsync(src.Path, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    // 哈希失败（文件被锁 / ACL）→ 保留 contentHash=""，路径解析仍可兜底。
                    LogHashFailed(_logger, ex, src.Path);
                    hash = "";
                }
            }
            dto.Sources.Add(new BundleSourceDto
            {
                SourceId = src.SourceId,
                DisplayName = src.DisplayName,
                Path = src.Path,
                ColorA = src.Color.A, ColorR = src.Color.R, ColorG = src.Color.G, ColorB = src.Color.B,
                StrokeStyle = src.StrokeStyle.ToString()!,
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
        dto.Viewports = new List<BundleViewportDto>();   // 窗口级 → 空列表
        dto.WatchedSignals = WatchedSignals
            .Where(r => !r.IsPlaceholder)
            .Select(r => new BundleWatchedSignalDto
            {
                CanIdHex = r.CanIdHex, MessageName = r.MessageName, SignalName = r.SignalName,
                Unit = r.Unit, SourceId = r.SourceId, Alias = r.Alias,
            }).ToList();
        dto.Groups = SignalGroups
            .Select(g => new BundleGroupDto
            {
                Id = g.Id, Name = g.Name, Notes = g.Notes, SignalKeys = g.SignalKeys.ToList(),
            }).ToList();
        return dto;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> OpenSessionAsync(string path)
    {
        // v3.x (会话状态剥离 Task 5 final, Critical #1): 全程 ConfigureAwait(true)，
        // 镜像 TraceSessionRegistry.LoadAsync 的内部契约与旧 VM.ApplySnapshotAsync。
        // registry 的 SourcesChanged 在调用者线程同步触发 → VM.OnRegistrySourcesChanged
        // 改 WPF 绑定集合（ChartViewModel.Series），且下方 WatchedSignals / SignalGroups
        // 恢复也改绑定集合——逃出 UI SynchronizationContext 会抛 NotSupportedException。
        var dto = await Task.Run(() => _library.Load(path)).ConfigureAwait(true);
        if (dto is null) return Array.Empty<string>();

        // 先卸载当前所有 source，使会话与 bundle 描述完全一致（保持卸载顺序确定）。
        var missing = new List<string>();
        foreach (var src in _registry.Sources.ToList())
            await _registry.UnloadAsync(src.SourceId).ConfigureAwait(true);

        // sourceId → DisplayName 映射，用于 load 后重新盖印 DisplayName / 反查 master。
        var nameBySourceId = dto.Sources.ToDictionary(s => s.SourceId, s => s.DisplayName, StringComparer.Ordinal);

        foreach (var bs in dto.Sources)
        {
            // v3.6.4 PATCH: 记录路径缺失且 bundle 携带 contentHash 时，先请 locator 按哈希重定位。
            var loadPath = bs.Path;
            if (!string.IsNullOrEmpty(bs.Path) && !File.Exists(bs.Path) && !string.IsNullOrEmpty(bs.ContentHash))
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
                // v3.6.0 MINOR T1.B: 从 bundle 恢复 per-source 过滤器 / DisplayName / 颜色。
                loaded.CanIdFilter = bs.CanIdFilter;
                if (!string.IsNullOrEmpty(bs.DisplayName) &&
                    bs.DisplayName != Path.GetFileNameWithoutExtension(bs.Path))
                    loaded.DisplayName = bs.DisplayName;
                // ARGB 全 0 = 未采集颜色 → 保留 registry 的 palette 色（兼容旧 bundle）。
                if (!(bs.ColorA == 0 && bs.ColorR == 0 && bs.ColorG == 0 && bs.ColorB == 0))
                    loaded.Color = new Color(bs.ColorR, bs.ColorG, bs.ColorB, bs.ColorA);
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

        // DBC 路径尽力恢复——缺失可接受（用户可手动重新加载）。
        if (!string.IsNullOrEmpty(dto.DbcPath) && File.Exists(dto.DbcPath))
        {
            try { await _dbcService.LoadAsync(dto.DbcPath).ConfigureAwait(true); }
            catch (FileNotFoundException) { /* bundle 引用已删除的 DBC —— 可接受，不记日志 */ }
            catch (Exception ex)
            {
                // 非"文件缺失"类失败记日志。service 不设 StatusMessage —— 那是 VM 的 UI 状态，不属于 service。
                LogBundleDbcLoadFailedInline(_logger, dto.DbcPath, ex);
            }
        }

        GlobalCanIdFilter = dto.GlobalCanIdFilter ?? "";
        // bundle 里的 SourceId 是录制时的 id；load 后 SourceId 变化，按 DisplayName 反查新 id。
        if (dto.Playback is { } pb && !string.IsNullOrEmpty(pb.MasterSourceId))
        {
            var newMaster = _registry.Sources.FirstOrDefault(s =>
                string.Equals(s.DisplayName, nameBySourceId.GetValueOrDefault(pb.MasterSourceId, ""), StringComparison.Ordinal));
            if (newMaster is not null) MasterSourceId = newMaster.SourceId;
        }

        // v3.x (会话状态剥离 Task 3, ledger Minor #2): 恢复 watch 列表 + 分组。
        // 原本在 VM.ApplySnapshotAsync (SessionFlow.cs:323-344) 恢复；本 task 删掉
        // ApplySnapshotAsync 后移到此处——service 是这两个集合的 owner，否则会话打开后
        // watch list/groups 静默丢失。anchor 重算 (RefreshAtAnchor) 是窗口级，留在 VM。
        WatchedSignals.Clear();
        if (dto.WatchedSignals is not null)
        {
            foreach (var w in dto.WatchedSignals)
            {
                var row = new WatchedSignalRow(
                    w.CanIdHex, w.MessageName, w.SignalName, w.Unit, w.SourceId);
                if (!string.IsNullOrEmpty(w.Alias))
                    row.Alias = w.Alias;
                WatchedSignals.Add(row);
            }
        }
        SignalGroups.Clear();
        if (dto.Groups is not null)
        {
            foreach (var g in dto.Groups)
            {
                SignalGroups.Add(new WatchedSignalGroup(
                    g.Id, g.Name, g.Notes, g.SignalKeys));
            }
        }
        // v3.x (会话状态剥离 Task 5 final, Important #2): watch/groups 恢复完成后
        // 通知开着的窗口——最后一次 SourcesChanged 驱动的 RefreshFrameCounts 发生在
        // 恢复之前，VM 必须补刷 FrameCount 与锚点（原 ApplySnapshotAsync 结尾的
        // RefreshAtAnchor 模式）。UI 线程触发（ConfigureAwait(true) 保证）。
        SessionRestored?.Invoke();
        return missing;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "BuildSnapshot: hashing failed for {Path}; bundle saved without contentHash")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle source missing or unreadable: {Path}")]
    private static partial void LogSourceMissing(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle source relocated via content hash: {OldPath} -> {NewPath}")]
    private static partial void LogRelocated(ILogger logger, string oldPath, string newPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle DBC load failed for {Path}")]
    private static partial void LogBundleDbcLoadFailedInline(ILogger logger, string path, Exception ex);
}
