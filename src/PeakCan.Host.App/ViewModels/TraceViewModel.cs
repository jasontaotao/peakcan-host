using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;

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
/// <b>2026-08-31 P1:</b> 视图层过滤——<see cref="Entries"/> 全量入列（非破坏性），
/// <see cref="FilterStateFlow.EntriesView"/>（<c>ListCollectionView</c>）做谓词过滤，
/// 改过滤可找回已入列帧。旧的 hex 前缀 <c>FilterText</c>/<c>FilteredCount</c> 已移除。
/// </para>
/// </summary>
public sealed partial class TraceViewModel : ObservableObject
{
    /// <summary>
    /// 无参 ctor（DI 循环规避设计）：建 <see cref="EntriesView"/>（同线程）并初始化状态文本。
    /// <c>DbcService</c> 经 <see cref="DbcBindingFlow.BindDbc"/> 属性注入。
    /// </summary>
    public TraceViewModel()
    {
        EntriesView = new System.Windows.Data.ListCollectionView(Entries);
        UpdateStatusText();
    }

    /// <summary>
    /// Backing store of trace rows. Mutated only on the WPF UI thread via
    /// <see cref="AppendBatchAsync"/>; reads from any thread are safe
    /// because the DataGrid marshals binding reads to the UI thread.
    /// </summary>
    public ObservableCollection<TraceEntry> Entries { get; } = new();

    /// <summary>
    /// FIFO trim threshold. When <see cref="Entries"/>.Count exceeds this
    /// value after a batch is appended, the oldest rows are removed
    /// (from index 0) until the count is back at the cap. Default 5_000
    /// (2026-08-31 P1，原 1_000 提升，配合视图层过滤 + 工具栏可调输入框
    /// <see cref="MaxRowsText"/>，校验范围 [100, 50000])。
    /// </summary>
    [ObservableProperty]
    private int _maxRows = 5_000;

    /// <summary>Total frames received (including any display-filtered rows).</summary>
    [ObservableProperty]
    private long _totalFrameCount;

    /// <summary>
    /// When true, only error frames are shown in the trace (并入
    /// <see cref="TraceFilterSpec.ErrorsOnly"/>，经 <c>TryRebuildSpec</c> 生效)。
    /// </summary>
    [ObservableProperty]
    private bool _showErrorsOnly;

    /// <summary>
    /// Channel filter. null = show all channels (零回归). Set to a ChannelId to
    /// suppress frames from other channels in the trace (并入
    /// <see cref="TraceFilterSpec.Channel"/>，经 <c>TryRebuildSpec</c> 生效）。
    /// 视图层过滤（非破坏性），数据平面 (ChannelRouter) 不变。
    /// </summary>
    [ObservableProperty]
    private ChannelId? _channelFilter;

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
        TotalFrameCount = 0;
        _messageCounts.Clear();
        // v1.2.11: drop pending-decode entries so stale lookups don't fill
        // Decoded on rows the user has already discarded.
        _pendingDecode.Clear();
        // 统计/状态随入列清空同步刷新。
        if (StatsExpanded) RefreshStats();
        UpdateStatusText();
    }

    // === Flow B methods moved to TraceViewModel/HighlightFilterFlow.cs (W19 Task 2) ===
    // === Flow C methods moved to TraceViewModel/ExportFlow.cs (W19 Task 3) ===
}
