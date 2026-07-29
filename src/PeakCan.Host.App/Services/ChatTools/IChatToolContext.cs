using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Bridge between a chat tool and the <see cref="TraceViewerViewModel"/>
/// state the tools need to read/mutate (watch list, anchors, DBC, seek).
/// </summary>
/// <remarks>
/// Defined in the App layer (not Core) because <see cref="WatchedSignalRow"/>
/// is an App-layer type. The VM implements this interface; tools inject it
/// and are unit-testable with a fake context (no WPF required).
/// <para>
/// <b>Threading:</b> In production, tools are invoked on the WPF UI thread
/// (see <c>ChatFlow.RunChatLoopAsync</c> — <c>ConfigureAwait(true)</c>).
/// <see cref="AddWatchedSignals"/>, <see cref="RefreshAtAnchor"/>,
/// <see cref="RefreshAtAnchorBlue"/>, and <see cref="Seek"/> mutate
/// UI-affined state directly. The VM implementation also supports
/// thread-pool callers via <c>Dispatcher.InvokeAsync</c> for future
/// off-UI-thread invocation.
/// </para>
/// </remarks>
public interface IChatToolContext
{
    /// <summary>Current green-anchor timestamp in seconds. <c>NaN</c> = no green anchor set.</summary>
    double AnchorTimestampSeconds { get; }

    /// <summary>Current blue-anchor timestamp in seconds. <c>NaN</c> = no blue anchor set.</summary>
    double BlueAnchorTimestampSeconds { get; }

    /// <summary>Currently loaded DBC document, or null if none loaded.</summary>
    DbcDocument? CurrentDbc { get; }

    /// <summary>Read-only view of the current watch list.</summary>
    IReadOnlyList<WatchedSignalRow> WatchedSignals { get; }

    /// <summary>Add rows to the watch list and synchronously recompute anchor
    /// values for the new rows using the current anchor timestamps. VM
    /// implementation marshals to the UI thread.</summary>
    void AddWatchedSignals(IEnumerable<WatchedSignalRow> rows);

    /// <summary>v12: Remove a single signal from the watch list by its
    /// SignalKey. Returns true if a row was removed. VM marshals to UI
    /// thread and re-runs RefreshAtAnchor for remaining rows.</summary>
    bool RemoveWatchedSignal(string signalKey);

    /// <summary>Recompute every watch row's green-anchor value at
    /// <paramref name="timestampSeconds"/>. Idempotent (passing the current
    /// anchor ts just re-decodes new rows). VM marshals to UI thread.</summary>
    void RefreshAtAnchor(double timestampSeconds);

    /// <summary>Sister of <see cref="RefreshAtAnchor"/> for the blue anchor.</summary>
    void RefreshAtAnchorBlue(double timestampSeconds);

    /// <summary>Seek the master trace source to <paramref name="timestampSeconds"/>.
    /// Returns false if no master source is loaded.</summary>
    bool Seek(double timestampSeconds);

    // === v12 Step 0: Context queries ===

    /// <summary>Snapshot of trace session metadata (duration, sources,
    /// DBC status, current timestamp). Null-safe when no trace loaded.</summary>
    TraceInfo GetTraceInfo();

    /// <summary>Snapshot of the loaded DBC (message/signal counts, node
    /// list). Returns zero counts when no DBC is loaded.</summary>
    DbcInfo GetDbcInfo();

    // === v12 Step 0: Group management ===

    /// <summary>Creates a signal group, optionally pre-populated with
    /// signal keys. Returns the new group's ID.</summary>
    string CreateGroup(string name, IReadOnlyList<string>? signalKeys);

    /// <summary>Adds signal keys to an existing group. Returns the count
    /// actually added (skips keys already present).</summary>
    int AddToGroup(string groupId, IReadOnlyList<string> signalKeys);

    /// <summary>Removes signal keys from a group. Returns the count
    /// actually removed.</summary>
    int RemoveFromGroup(string groupId, IReadOnlyList<string> signalKeys);

    /// <summary>Attaches analysis notes to a group.</summary>
    void SetGroupNotes(string groupId, string notes);

    /// <summary>Sets a display alias for a watched signal. Pass null to
    /// clear.</summary>
    void SetSignalAlias(string signalKey, string? alias);

    /// <summary>Read-only view of all signal groups.</summary>
    IReadOnlyList<WatchedSignalGroup> SignalGroups { get; }

    /// <summary>v12 Step 3: defensive copy of frames for a source.
    /// Returns empty if sourceId is unknown. Used by analysis tools
    /// (get_signal_overview, anomaly_scan, search_signal_trace,
    /// analyze_timing_sequence) to decode signal values.</summary>
    IReadOnlyList<ReplayFrame> GetFrames(string sourceId);
}
