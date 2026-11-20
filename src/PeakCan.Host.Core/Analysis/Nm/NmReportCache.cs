namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmCacheKey(
    string SourceId,
    ushort? Channel,
    int FrameCount,
    string DbcHash,
    string SupplementHash,
    double Start,
    double End,
    string EngineVersion,
    // 同 start/end 但窗口标签不同（如 "full" vs 自定义窗口）也是不同的报告。
    string WindowRef = "full");

/// <summary>
/// 进程内报告缓存。线程安全（分析可能在 Task.Run 里完成）。
/// 换 DBC / sidecar / engine 版本、load/unload 都必须失效。
/// </summary>
public sealed class NmReportCache
{
    private readonly Dictionary<NmCacheKey, NmReport> _items = new();
    private readonly Lock _gate = new();

    public bool TryGet(NmCacheKey key, out NmReport report)
    {
        lock (_gate) return _items.TryGetValue(key, out report!);
    }

    public void Put(NmCacheKey key, NmReport report)
    {
        lock (_gate) _items[key] = report;
    }

    public void Invalidate(string sourceId)
    {
        lock (_gate)
        {
            foreach (var key in _items.Keys.Where(k => k.SourceId == sourceId).ToArray())
                _items.Remove(key);
        }
    }

    public void Invalidate()
    {
        lock (_gate) _items.Clear();
    }
}
