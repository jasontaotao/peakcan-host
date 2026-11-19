using System.IO;
using System.Security.Cryptography;
using System.Text;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
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

    async Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
    {
        var sourceId = string.IsNullOrEmpty(query.SourceId) ? MasterSourceId : query.SourceId;
        if (string.IsNullOrEmpty(sourceId))
            throw new InvalidOperationException("没有已加载的 trace source");

        var service = _registry.GetService(sourceId)
            ?? throw new InvalidOperationException($"unknown source_id: {sourceId}");

        // 快照必须在 UI 线程取：LoadedFrames 返回内部只读列表（加载后不可变）。
        var frames = service.LoadedFrames;
        var dbc = _dbcService.Current;
        var dbcPath = string.IsNullOrEmpty(dbc?.SourcePath) ? null : dbc.SourcePath;
        var dbcHash = ComputeNmDbcHash(dbc, dbcPath);

        var supplement = dbcPath is null ? null : NmSidecarStore.Load(dbcPath);
        var profile = NmSupplementMerger.Merge(NmProfileBuilder.Build(dbc, dbcPath), supplement);

        var start = query.TStart ?? 0.0;
        var end = query.TEnd ?? (frames.Count > 0 ? frames[^1].Timestamp + 1e-9 : 0.0);
        var key = new NmCacheKey(sourceId, query.Channel, frames.Count, dbcHash,
            NmSupplementHash(profile), start, end, NmAnalyzer.EngineVersion);

        if (_nmCache.TryGet(key, out var cached))
            return new NmToolSnapshot(sourceId, query.Channel, dbcPath, dbcHash, profile, cached, frames);

        var request = new NmAnalysisRequest(
            sourceId, query.Channel, frames, dbc, profile, start, end, query.WindowRef ?? "full");

        var report = await Task.Run(() => NmAnalyzer.Analyze(request, ct), ct).ConfigureAwait(false);
        _nmCache.Put(key, report);
        return new NmToolSnapshot(sourceId, query.Channel, dbcPath, dbcHash, profile, report, frames);
    }

    private string ComputeNmDbcHash(DbcDocument? dbc, string? path)
    {
        if (dbc is null) return "none";
        if (path is null) return "inline";
        if (_nmDbcHashPath == path && _nmDbcHash is not null) return _nmDbcHash;
        try
        {
            _nmDbcHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            _nmDbcHashPath = path;
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
        var payload = string.Join("|",
            profile.Layout?.NidByte, profile.Layout?.CbvByte,
            profile.Layout?.UserDataOffset, profile.Layout?.UserDataLength,
            profile.Timings.CycleMs, profile.Timings.TimeoutMs,
            profile.Timings.RepeatMessageMs, profile.Timings.WaitBusSleepMs,
            profile.Features?.PnEnabled, profile.Features?.ActiveWakeupBitEnabled,
            string.Join(",", profile.Messages.Where(m => m.IsNm).Select(m => m.CanIdHex)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
