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
    private string? _nmDbcHash;
    private string? _nmDbcHashPath;
    private long _nmDbcHashLength;
    private DateTime _nmDbcHashWriteUtc;

    async Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
    {
        var sourceId = string.IsNullOrEmpty(query.SourceId) ? MasterSourceId : query.SourceId;
        if (string.IsNullOrEmpty(sourceId))
            throw new InvalidOperationException("没有已加载的 trace source");

        var service = _registry.GetService(sourceId)
            ?? throw new InvalidOperationException($"unknown source_id: {sourceId}");

        // 快照必须在 UI 线程取：LoadedFrames 返回内部只读列表（加载后不可变）。
        var frames = service.LoadedFrames;
        // 多通道且未指定 channel 时分析器只会返回空选择；这里直接报错，
        // 避免工具/面板把「不知道看哪个通道」呈现成「没有 NM」。
        EnsureSingleChannel(frames, query.Channel);
        var dbc = _dbcService.Current;
        var dbcPath = string.IsNullOrEmpty(dbc?.SourcePath) ? null : dbc.SourcePath;
        var dbcHash = ComputeNmDbcHash(dbc, dbcPath);

        var supplement = dbcPath is null ? null : NmSidecarStore.Load(dbcPath);
        var profile = NmSupplementMerger.Merge(NmProfileBuilder.Build(dbc, dbcPath), supplement);

        var start = query.TStart ?? 0.0;
        var end = query.TEnd ?? (frames.Count > 0 ? frames[^1].Timestamp + 1e-9 : 0.0);
        var windowRef = query.WindowRef ?? "full";
        var key = new NmCacheKey(sourceId, query.Channel, frames.Count, dbcHash,
            NmSupplementHash(profile), start, end, NmAnalyzer.EngineVersion, windowRef);

        if (_nmCache.TryGet(key, out var cached))
            return new NmToolSnapshot(sourceId, query.Channel, dbcPath, dbcHash, profile, cached, frames);

        var request = new NmAnalysisRequest(
            sourceId, query.Channel, frames, dbc, profile, start, end, windowRef);

        var report = await Task.Run(() => NmAnalyzer.Analyze(request, ct), ct).ConfigureAwait(false);
        _nmCache.Put(key, report);
        return new NmToolSnapshot(sourceId, query.Channel, dbcPath, dbcHash, profile, report, frames);
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

        var payload = string.Join("|",
            layout?.NidByte, layout?.CbvByte,
            layout?.UserDataOffset, layout?.UserDataLength,
            timings.CycleMs, timings.TimeoutMs,
            timings.RepeatMessageMs, timings.WaitBusSleepMs,
            timings.ImmediateCycleMs, timings.ImmediateCount,
            features?.PnEnabled, features?.ActiveWakeupBitEnabled,
            features?.RepeatMsgIndEnabled,
            profile.IdMapping,
            nodes, messages);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
