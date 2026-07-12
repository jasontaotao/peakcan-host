using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>Per-message-ID statistics for the Trace tab.</summary>
public sealed record MessageIdStat(
    string IdHex,
    uint RawId,
    long Count,
    double Percent);

/// <summary>
/// Backing view model for the Trace tab. Owns an
/// <see cref="ObservableCollection{TraceEntry}"/> that the WPF
/// <c>DataGrid</c> in <c>Views/TraceView.xaml</c> binds to.
/// <para>
/// <b>Dispatcher contract:</b> the WPF UI thread is the only thread that
/// may mutate an <see cref="ObservableCollection{T}"/> that's already
/// bound to a <c>ItemsControl</c>. <see cref="AppendBatchAsync"/> is
/// called from the <see cref="Services.TraceService"/> background loop,
/// so it must marshal back to the UI thread via
/// <c>Application.Current.Dispatcher</c>. The contract is:
/// </para>
/// <list type="bullet">
///   <item>In production, <c>Application.Current</c> is always non-null
///     (the WPF app owns the singleton), so the dispatcher is always
///     available and the batch is appended on the UI thread.</item>
///   <item>In test contexts, <c>Application.Current</c> is null (xunit
///     has no <c>Application</c> instance). The method then returns
///     <see cref="Task.CompletedTask"/> without throwing or modifying
///     <see cref="Entries"/>. This is documented and pinned by
///     <c>TraceViewModelTests.AppendBatch_With_Null_Dispatcher_*</c>.</item>
/// </list>
/// <para>
/// <b>Why a parameterless constructor?</b> <c>AppHostBuilder</c> registers
/// this VM as a singleton via <c>AddSingleton&lt;TraceViewModel&gt;()</c>;
/// a parameterless ctor avoids a DI circular-reference (the
/// <see cref="Services.TraceService"/> depends on the VM and the VM is
/// resolved before the service starts).
/// </para>
/// <para>
/// <b>v0.6.0 frame filter:</b> <see cref="FilterText"/> accepts a hex
/// prefix pattern (e.g. <c>"1A"</c> matches IDs 0x1A0–0x1AF). When
/// non-empty, only matching frames are appended to <see cref="Entries"/>.
/// <see cref="FilteredCount"/> tracks how many frames were suppressed.
/// </para>
/// </summary>
public sealed partial class TraceViewModel : ObservableObject
{
    /// <summary>
    /// Backing store of trace rows. Mutated only on the WPF UI thread via
    /// <see cref="AppendBatchAsync"/>; reads from any thread are safe
    /// because the DataGrid marshals binding reads to the UI thread.
    /// </summary>
    public ObservableCollection<TraceEntry> Entries { get; } = new();

    /// <summary>
    /// FIFO trim threshold. When <see cref="Entries"/>.Count exceeds this
    /// value after a batch is appended, the oldest rows are removed
    /// (from index 0) until the count is back at the cap. Default 1_000
    /// keeps the WPF DataGrid render cost manageable under sustained
    /// high frame rates. 1_000 rows × 20 px = 20 k px of virtualized
    /// content, well within the recycling virtualization budget.
    /// The toolbar's "cap: {N} rows" label displays the current value
    /// (read-only); programmatic mutation via the generated setter is
    /// supported but no UI editor ships today. A future PATCH can
    /// add a numeric input bound TwoWay to <see cref="MaxRows"/> if
    /// user-tunable cap is wanted.
    /// </summary>
    [ObservableProperty]
    private int _maxRows = 1_000;

    /// <summary>
    /// v0.6.0: hex prefix filter for CAN IDs. Empty = show all.
    /// E.g. "1A" matches 0x1A0–0x1AF; "1A3" matches exactly 0x1A3.
    /// </summary>
    [ObservableProperty]
    private string _filterText = "";

    /// <summary>Count of frames suppressed by the current filter.</summary>
    [ObservableProperty]
    private long _filteredCount;

    /// <summary>Total frames received (including filtered).</summary>
    [ObservableProperty]
    private long _totalFrameCount;

    /// <summary>
    /// Hex prefix for highlighting matching rows. Empty = no highlight.
    /// Matching rows get <see cref="TraceEntry.IsHighlighted"/> = true.
    /// </summary>
    [ObservableProperty]
    private string _highlightText = "";

    /// <summary>
    /// When true, only error frames are shown in the trace.
    /// </summary>
    [ObservableProperty]
    private bool _showErrorsOnly;

    /// <summary>
    /// When true, new frames are not appended to the trace.
    /// Counter updates still happen.
    /// </summary>
    [ObservableProperty]
    private bool _isPaused;

    // Per-message-ID counter. Key = raw CAN ID.
    private readonly Dictionary<uint, long> _messageCounts = new();

    // v1.2.11: pending entries awaiting DBC decode. ConcurrentDictionary
    // because DbcDecodeBackgroundService worker reads (TryCompletePending)
    // from its own thread while the UI thread mutates (AppendBatchAsync
    // Register, Clear, FIFO trim). The original Dictionary had a
    // cross-thread race per the v1.2.11 code review.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TraceEntryKey, TraceEntry> _pendingDecode = new();

    /// <summary>
    /// Read-only view of entries awaiting DBC decode. Consumed by
    /// <see cref="Services.DbcDecodeBackgroundService"/> to fill
    /// <see cref="TraceEntry.Decoded"/> without taking a write dependency
    /// on the trace VM.
    /// </summary>
    public IReadOnlyDictionary<TraceEntryKey, TraceEntry> PendingDecode => _pendingDecode;

    /// <summary>
    // === Flow A methods moved to TraceViewModel/ReceptionFlow.cs (W19 Task 1) ===
    /// <summary>Clear the trace entries and reset the filter counter.</summary>
    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        FilteredCount = 0;
        TotalFrameCount = 0;
        _messageCounts.Clear();
        // v1.2.11: drop pending-decode entries so stale lookups don't fill
        // Decoded on rows the user has already discarded.
        _pendingDecode.Clear();
    }

    // === Flow B methods moved to TraceViewModel/HighlightFilterFlow.cs (W19 Task 2) ===
    // === Flow C methods moved to TraceViewModel/ExportFlow.cs (W19 Task 3) ===
}
