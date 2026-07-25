using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;

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
/// <b>Threading:</b> <see cref="AddWatchedSignals"/>, <see cref="RefreshAtAnchor"/>,
/// <see cref="RefreshAtAnchorBlue"/>, and <see cref="Seek"/> mutate UI-affined
/// state. The VM implementation marshals to the UI thread internally
/// (<c>Dispatcher.InvokeAsync</c>), so callers on the thread-pool are safe.
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

    /// <summary>Recompute every watch row's green-anchor value at
    /// <paramref name="timestampSeconds"/>. Idempotent (passing the current
    /// anchor ts just re-decodes new rows). VM marshals to UI thread.</summary>
    void RefreshAtAnchor(double timestampSeconds);

    /// <summary>Sister of <see cref="RefreshAtAnchor"/> for the blue anchor.</summary>
    void RefreshAtAnchorBlue(double timestampSeconds);

    /// <summary>Seek the master trace source to <paramref name="timestampSeconds"/>.
    /// Returns false if no master source is loaded.</summary>
    bool Seek(double timestampSeconds);
}
