namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 报告缓存 key（spec §10.1）。
/// <para>
/// <b>为什么没有 DBC hash</b>：DBC 与 sidecar 的报文/节点/布局/时序已被展平进
/// <see cref="NmProfile"/>，其语义由 <see cref="CanonicalProfileHash"/> 表达。
/// 再拼 DBC 内容哈希会造成「DBC 只改注释也失效」（spec §10.1 明令移除）。
/// </para>
/// <para>
/// <b>为什么保留 source/channel/窗口/帧数</b>：这些不是 profile 的重复。
/// 同一 source 重新加载成帧数相同、起止相同的新 trace，profile 可以逐字节相同，
/// 但报告必须重算——<see cref="TraceLoadVersion"/> 正是这个内容版本的载体。
/// </para>
/// </summary>
/// <param name="TraceLoadVersion">
/// trace 内容版本。取「该 source 的加载代数」而非对全部帧做哈希：
/// 后者成本随帧数线性增长，而本仓库的 source 每次 <c>LoadAsync</c> 都分配新
/// sourceId（见 <c>TraceSessionRegistry.LoadAsync</c>），同一 sourceId 重载今天不会
/// 发生——spec §10.1 自身也给了「或 load generation」这个替代方案。
/// 该字段存在是为了让「将来复用 sourceId 重载」这件事一旦发生就自动失效，
/// 而不是静默复用旧报告。
/// </param>
/// <param name="CanonicalProfileHash">
/// profile 的 canonical 语义摘要（<see cref="NmCanonicalProfileHash.Compute"/>）。
/// </param>
public sealed record NmCacheKey(
    string SourceId,
    ushort? Channel,
    double Start,
    double End,
    string WindowRef,
    int FrameCount,
    int TraceLoadVersion,
    string CanonicalProfileHash,
    string EngineVersion);

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
