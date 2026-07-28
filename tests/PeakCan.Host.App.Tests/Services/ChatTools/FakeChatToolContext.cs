using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
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

    public void AddWatchedSignals(IEnumerable<WatchedSignalRow> rows) => AddedRows.AddRange(rows);
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
}
