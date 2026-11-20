using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

/// <summary>
/// In-memory fake <see cref="IChatToolContext"/> for unit-testing chat tools
/// without WPF or a real VM. Records every mutating call so tests can assert
/// invocation order and arguments.
/// </summary>
internal sealed class FakeChatToolContext : IChatToolContext
{
    public double AnchorTimestampSeconds { get; set; } = double.NaN;
    public double BlueAnchorTimestampSeconds { get; set; } = double.NaN;
    public DbcDocument? CurrentDbc { get; set; }
    public List<WatchedSignalRow> WatchedSignals { get; } = new();
    IReadOnlyList<WatchedSignalRow> IChatToolContext.WatchedSignals => WatchedSignals;

    public List<WatchedSignalRow> AddedRows { get; } = new();
    public List<double> RefreshAtAnchorCalls { get; } = new();
    public List<double> RefreshAtAnchorBlueCalls { get; } = new();
    public List<double> SeekCalls { get; } = new();
    public bool SeekResult { get; set; } = true;

    public void AddWatchedSignals(IEnumerable<WatchedSignalRow> rows)
    {
        AddedRows.AddRange(rows);
        // v12: mirror the real VM which now refreshes anchors inside
        // AddWatchedSignals (same Dispatcher.Invoke) to avoid DataGrid
        // ItemContainerGenerator count mismatch.
        if (!double.IsNaN(AnchorTimestampSeconds))
            RefreshAtAnchor(AnchorTimestampSeconds);
        if (!double.IsNaN(BlueAnchorTimestampSeconds))
            RefreshAtAnchorBlue(BlueAnchorTimestampSeconds);
    }
    public void RefreshAtAnchor(double timestampSeconds) => RefreshAtAnchorCalls.Add(timestampSeconds);
    public void RefreshAtAnchorBlue(double timestampSeconds) => RefreshAtAnchorBlueCalls.Add(timestampSeconds);
    public bool Seek(double timestampSeconds)
    {
        SeekCalls.Add(timestampSeconds);
        return SeekResult;
    }

    // === v12 new members ===

    public List<string> RemovedSignalKeys { get; } = new();
    public bool RemoveWatchedSignal(string signalKey)
    {
        RemovedSignalKeys.Add(signalKey);
        int idx = WatchedSignals.FindIndex(r => r.SignalKey == signalKey);
        if (idx >= 0) { WatchedSignals.RemoveAt(idx); return true; }
        return false;
    }

    public TraceInfo TraceInfoValue { get; set; } = new(0, 0, false, null, 0, null, Array.Empty<TraceSourceInfo>());
    public TraceInfo GetTraceInfo() => TraceInfoValue;

    public DbcInfo DbcInfoValue { get; set; } = new(null, 0, 0, Array.Empty<string>(), null);
    public DbcInfo GetDbcInfo() => DbcInfoValue;

    public List<WatchedSignalGroup> SignalGroups { get; } = new();
    IReadOnlyList<WatchedSignalGroup> IChatToolContext.SignalGroups => SignalGroups;

    public string CreateGroup(string name, IReadOnlyList<string>? signalKeys)
    {
        var g = new WatchedSignalGroup(Guid.NewGuid().ToString("N"), name, null,
            signalKeys is null ? Array.Empty<string>() : signalKeys.ToList());
        SignalGroups.Add(g);
        return g.Id;
    }

    public int AddToGroup(string groupId, IReadOnlyList<string> signalKeys)
    {
        int i = SignalGroups.FindIndex(g => g.Id == groupId);
        if (i < 0) return 0;
        var toAdd = signalKeys.Except(SignalGroups[i].SignalKeys).ToList();
        SignalGroups[i] = SignalGroups[i] with { SignalKeys = SignalGroups[i].SignalKeys.Concat(toAdd).ToList() };
        return toAdd.Count;
    }

    public int RemoveFromGroup(string groupId, IReadOnlyList<string> signalKeys)
    {
        int i = SignalGroups.FindIndex(g => g.Id == groupId);
        if (i < 0) return 0;
        var remaining = SignalGroups[i].SignalKeys.Except(signalKeys).ToList();
        int removed = SignalGroups[i].SignalKeys.Count - remaining.Count;
        SignalGroups[i] = SignalGroups[i] with { SignalKeys = remaining };
        return removed;
    }

    public void SetGroupNotes(string groupId, string notes)
    {
        int i = SignalGroups.FindIndex(g => g.Id == groupId);
        if (i >= 0) SignalGroups[i] = SignalGroups[i] with { Notes = notes };
    }

    public Dictionary<string, string?> AliasSet { get; } = new();
    public void SetSignalAlias(string signalKey, string? alias) => AliasSet[signalKey] = alias;

    public List<ReplayFrame> Frames { get; } = new();
    public IReadOnlyList<ReplayFrame> GetFrames(string sourceId) => Frames;

    public NmToolSnapshot? NmSnapshotValue { get; set; }
    public Exception? NmSnapshotException { get; set; }

    /// <summary>每次 <see cref="IChatToolContext.GetNmSnapshotAsync"/> 的入参，
    /// 用于断言工具是否把窗口参数原样透传。</summary>
    public List<NmToolQuery> NmQueries { get; } = new();

    /// <summary>模拟 NmFlow 的按查询缓存：开启后同一 query 只"分析"一次，
    /// 用于断言钻取不会重新扫帧。</summary>
    public bool CacheNmSnapshotsByQuery { get; set; }

    /// <summary>缓存未命中的次数（= 真正取快照/分析的次数）。</summary>
    public int NmAnalysisCount { get; private set; }

    /// <summary>已加载 trace source 的 id 列表（spec §8.1 多源显式校验）。</summary>
    public List<string> LoadedSourceIds { get; } = new();
    IReadOnlyList<string> IChatToolContext.LoadedSourceIds => LoadedSourceIds;

    private readonly Dictionary<NmToolQuery, NmToolSnapshot> _nmByQuery = new();

    Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
    {
        NmQueries.Add(query);
        if (NmSnapshotException is not null) throw NmSnapshotException;

        if (CacheNmSnapshotsByQuery && _nmByQuery.TryGetValue(query, out var cached))
            return Task.FromResult(cached);

        NmAnalysisCount++;
        if (NmSnapshotValue is null) throw new InvalidOperationException("NmSnapshotValue not set");
        if (CacheNmSnapshotsByQuery) _nmByQuery[query] = NmSnapshotValue;
        return Task.FromResult(NmSnapshotValue);
    }
}
