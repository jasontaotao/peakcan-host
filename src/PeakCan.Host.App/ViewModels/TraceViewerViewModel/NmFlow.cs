using System.IO;
using System.Security.Cryptography;
using System.Text;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// NM 分析入口：取快照、合并 sidecar、查缓存、后台执行。
/// 工具与面板都走这里，保证两者看到同一份报告。
/// </summary>
public sealed partial class TraceViewerViewModel
{
    private readonly NmReportCache _nmCache = new();
    // DBC 哈希 memo 的实例字段：重活已移入 Task.Run，可能被并发调用读写，必须加锁。
    private readonly Lock _nmDbcHashGate = new();
    private string? _nmDbcHash;
    private string? _nmDbcHashPath;
    private long _nmDbcHashLength;
    private DateTime _nmDbcHashWriteUtc;

    /// <summary>
    /// 测试接缝：在后台线程真正开始读盘/哈希前触发一次，供测试确认重活不在调用线程。
    /// 生产路径保持 null。
    /// </summary>
    internal Action? NmHeavyWorkProbe { get; set; }

    async Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
    {
        // UI 线程只做快照捕获：帧列表加载后不可变，路径/窗口都是只读值。
        var sourceId = string.IsNullOrEmpty(query.SourceId) ? MasterSourceId : query.SourceId;
        if (string.IsNullOrEmpty(sourceId))
            throw new InvalidOperationException("没有已加载的 trace source");

        var service = _registry.GetService(sourceId)
            ?? throw new InvalidOperationException($"unknown source_id: {sourceId}");

        var frames = service.LoadedFrames;
        // 多通道且未指定 channel 时分析器只会返回空选择；这里直接报错，
        // 避免工具/面板把「不知道看哪个通道」呈现成「没有 NM」。
        EnsureSingleChannel(frames, query.Channel);
        var dbc = _dbcService.Current;
        var dbcPath = string.IsNullOrEmpty(dbc?.SourcePath) ? null : dbc.SourcePath;
        var start = query.TStart ?? 0.0;
        var end = query.TEnd ?? (frames.Count > 0 ? frames[^1].Timestamp + 1e-9 : 0.0);
        var windowRef = query.WindowRef ?? "full";

        // 整份 DBC 读盘 + SHA256、sidecar 读盘、profile 构建/合并都是重活：
        // 全部放进 Task.Run，UI 线程只等待结果（spec §10：UI 线程单次阻塞 < 100ms）。
        return await Task.Run(() =>
        {
            var dbcHash = ComputeNmDbcHash(dbc, dbcPath);

            var supplement = dbcPath is null ? null : NmSidecarStore.Load(dbcPath);
            // spec §6.6.6：sidecar 记录的是另一份 DBC 时，布局字节与报文选择器都按旧 DBC
            // 绑定，必须一并丢弃；timings/features/nodes 等仍按用户输入合并。
            var staleSidecar = supplement?.DbcHash is { Length: > 0 } sidecarHash
                && !string.Equals(sidecarHash, dbcHash, StringComparison.OrdinalIgnoreCase);
            var profile = NmSupplementMerger.Merge(
                NmProfileBuilder.Build(dbc, dbcPath), supplement,
                applyLayout: !staleSidecar, applySelectors: !staleSidecar);
            if (staleSidecar)
            {
                profile = AppendDbcHashConflict(profile, supplement!.DbcHash!, dbcHash);
            }

            var key = new NmCacheKey(sourceId, query.Channel, frames.Count, dbcHash,
                NmSupplementHash(profile), start, end, NmAnalyzer.EngineVersion, windowRef);

            // 缓存读写仍在同一后台临界区内，保证命中/写入不会与 UI 线程交错。
            if (_nmCache.TryGet(key, out var cached))
                return new NmToolSnapshot(
                    sourceId, query.Channel, dbcPath, dbcHash, profile,
                    WithStaleSidecarAssumption(cached, staleSidecar), frames);

            var request = new NmAnalysisRequest(
                sourceId, query.Channel, frames, dbc, profile, start, end, windowRef);

            var report = NmAnalyzer.Analyze(request, ct);
            StoreNmReport(key, report);
            return new NmToolSnapshot(
                sourceId, query.Channel, dbcPath, dbcHash, profile,
                WithStaleSidecarAssumption(report, staleSidecar), frames);
        }, ct).ConfigureAwait(false);
    }

    private static readonly string[] DbcHashConflictSources = ["sidecar", "dbc"];

    /// <summary>DBC hash 不匹配时把冲突写进 profile，使 NM_CONFIG_CONFLICT finding 与面板黄色条出现。</summary>
    private static NmProfile AppendDbcHashConflict(NmProfile profile, string oldHash, string newHash)
        => profile with
        {
            Conflicts = profile.Conflicts
                .Append(new NmConfigConflict(
                    "dbc_hash", new[] { oldHash, newHash }, DbcHashConflictSources))
                .ToArray(),
        };

    /// <summary>
    /// 写入报告缓存。spec §9.4：取消/超时产生的 partial 结果只属于本次请求，
    /// 不得写入缓存供后续请求（含 AI 工具）当成完整结果复用；工具自身被取消时
    /// 仍会直接拿到这份 partial 报告并原样输出 unknowns/assumptions。
    /// </summary>
    internal void StoreNmReport(NmCacheKey key, NmReport report)
    {
        if (report.IsPartial) return;
        _nmCache.Put(key, report);
    }

    /// <summary>测试接缝：验证 partial 结果确实没有进入缓存。</summary>
    internal bool TryGetCachedNmReport(NmCacheKey key, out NmReport report)
        => _nmCache.TryGet(key, out report);

    private const string StaleSidecarAssumption =
        "sidecar 属于另一份 DBC，布局未套用（stale_sidecar）";

    private static NmReport WithStaleSidecarAssumption(NmReport report, bool staleSidecar)
    {
        if (!staleSidecar) return report;
        return report.Assumptions.Contains(StaleSidecarAssumption, StringComparer.Ordinal)
            ? report
            : report with
            {
                Assumptions = report.Assumptions.Append(StaleSidecarAssumption).ToArray(),
            };
    }

    /// <summary>
    /// 多通道 trace 未指定 channel 时无法给出唯一 NM 结论：明确报错并列出可用通道。
    /// 指定了 channel 或只有一个通道时放行。
    /// </summary>
    internal static void EnsureSingleChannel(IReadOnlyList<ReplayFrame> frames, ushort? channel)
    {
        if (channel is not null) return;
        var channels = frames.Select(f => f.Channel).Distinct().OrderBy(c => c).ToArray();
        if (channels.Length > 1)
            throw new InvalidOperationException(
                $"多通道 trace（可用 channel: {string.Join(", ", channels)}），请指定 channel");
    }

    private string ComputeNmDbcHash(DbcDocument? dbc, string? path)
    {
        if (dbc is null) return "none";
        if (path is null) return "inline";

        NmHeavyWorkProbe?.Invoke();
        lock (_nmDbcHashGate)
        {
            try
            {
                // memo 必须随文件内容变化失效：同路径重载 DBC 后长度/mtime 会变，
                // 只比较 path 会把旧 DBC 的 report 复用给新 profile。
                var info = new FileInfo(path);
                var length = info.Length;
                var writeUtc = info.LastWriteTimeUtc;
                if (_nmDbcHashPath == path && _nmDbcHash is not null
                    && _nmDbcHashLength == length && _nmDbcHashWriteUtc == writeUtc)
                {
                    return _nmDbcHash;
                }

                _nmDbcHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                _nmDbcHashPath = path;
                _nmDbcHashLength = length;
                _nmDbcHashWriteUtc = writeUtc;
                return _nmDbcHash;
            }
            catch (IOException)
            {
                return "unreadable";
            }
            catch (UnauthorizedAccessException)
            {
                return "unreadable";
            }
        }
    }

    private static string NmSupplementHash(NmProfile profile)
    {
        var layout = profile.Layout;
        var timings = profile.Timings;
        var features = profile.Features;

        // Nodes / Messages 按稳定顺序拼接，避免集合顺序漂移产生无谓的缓存未命中。
        var nodes = string.Join(";", profile.Nodes
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .Select(n => $"{n.Name}/{n.StationAddress}/{n.NodeIdentifier}/{n.IsNmNode}"));

        var messages = string.Join(";", profile.Messages
            .Where(m => m.IsNm)
            .OrderBy(m => m.CanIdHex, StringComparer.Ordinal)
            .ThenBy(m => m.IsExtended)
            .Select(m => $"{m.CanIdHex}/{m.IsExtended}/{m.Name}/{m.Sender}/{m.Dlc}/{m.CycleMs}/{m.TimeoutMs}"));

        // 冲突集合也参与 key：stale sidecar 追加的 dbc_hash 冲突会改变分析结果，
        // 不能命中之前“无冲突”的报告缓存。
        var conflicts = string.Join(";", profile.Conflicts
            .Select(c => $"{c.Field}:{string.Join(",", c.Values)}:{string.Join(",", c.Sources)}"));

        var payload = string.Join("|",
            layout?.NidByte, layout?.CbvByte,
            layout?.UserDataOffset, layout?.UserDataLength,
            timings.CycleMs, timings.TimeoutMs,
            timings.RepeatMessageMs, timings.WaitBusSleepMs,
            timings.ImmediateCycleMs, timings.ImmediateCount,
            features?.PnEnabled, features?.ActiveWakeupBitEnabled,
            features?.RepeatMsgIndEnabled,
            profile.IdMapping,
            nodes, messages, conflicts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
