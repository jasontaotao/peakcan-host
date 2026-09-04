using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.App.Services.J1939;

namespace PeakCan.Host.App.ViewModels;

/// <summary>Trace Viewer 的 J1939 分析流（L2 重组视图 + L3 解码帧序列，spec §9.2/9.3）。</summary>
public sealed partial class TraceViewerViewModel
{
    // Task 12：离线重组服务（DI singleton；测试构造未注入时由 ctor 兜底空实例）。
    private readonly J1939ReassemblyService _j1939Reassembly;
    private IReadOnlyList<ReplayFrame>? _decodeFrames;

    /// <summary>
    /// 重组产出的虚拟帧（<see cref="J1939VirtualFrameMerger.ToVirtualFrames"/>）。
    /// watch 值刷新（RefreshFrameCounts）并入此列表，否则多帧报文（BRM/BCP 等）在
    /// 原始帧里只有 TP.CM/TP.DT、完整载荷仅存在于虚拟帧，watch 行 FrameCount 恒 0。
    /// 生命周期与 <see cref="_decodeFrames"/> 同步（无源时随之一并清空）。
    /// </summary>
    private IReadOnlyList<ReplayFrame> _j1939VirtualFrames = Array.Empty<ReplayFrame>();

    /// <summary>L2 重组消息行（完成时间升序）。</summary>
    public ObservableCollection<ReassembledJ1939Message> ReassembledMessages { get; } = new();

    /// <summary>双击 Seek 的目标行（DataGrid SelectedItem TwoWay 绑定 + CommandParameter 数据源）。</summary>
    [ObservableProperty]
    private ReassembledJ1939Message? _selectedReassembled;

    /// <summary>
    /// 信号解码路径的帧序列：原始帧 ∪ 完整重组的虚拟帧（按 Timestamp 稳定归并，同刻原始帧在前）。
    /// 未重组过时退回原始帧序列。帧计数类用途继续用 LoadedFrames，仅解码路径用本序列。
    /// </summary>
    public IReadOnlyList<ReplayFrame> DecodeFrames => _decodeFrames ?? _masterService?.LoadedFrames ?? Array.Empty<ReplayFrame>();

    /// <summary>
    /// 重组 + 重建解码帧序列（加载成功后调用；幂等）。无源（最后卸载后 master 恒为
    /// null）时清空 L2 行 + 虚拟帧输入后返回——Task 13 路由决策（stale-DecodeFrames）：
    /// 否则陈旧虚拟帧永久滞留、<see cref="DecodeFrames"/> 持续供给陈旧合并列表。
    /// </summary>
    [RelayCommand]
    private void RebuildJ1939Views()
    {
        if (_masterService is null)
        {
            ReassembledMessages.Clear();
            _decodeFrames = null;   // DecodeFrames 退化为原始帧序列（此处 master null → 空序列）
            _j1939VirtualFrames = Array.Empty<ReplayFrame>();
            return;
        }

        var raw = _masterService.LoadedFrames;
        var messages = _j1939Reassembly.Reassemble(raw);

        ReassembledMessages.Clear();
        foreach (var m in messages)
            ReassembledMessages.Add(m);

        // L3 解码帧序列（spec §9.3）：原始 ∪ 完整重组虚拟帧，同刻原始帧在前；
        // Task 13 注入点经 DecodeFrames 供给解码路径（SamplingTableFlow / ChartSeriesFlow）。
        _j1939VirtualFrames = J1939VirtualFrameMerger.ToVirtualFrames(messages);
        _decodeFrames = J1939VirtualFrameMerger.Merge(raw, messages);
    }

    /// <summary>双击重组行 → 跳转 TP.CM 帧（复用 IChatToolContext.Seek 同路径的 _masterService.Seek）。</summary>
    [RelayCommand]
    private void SeekToReassembled(ReassembledJ1939Message? message)
    {
        if (message is null || _masterService is null)
            return;
        // 本地副本：字段的可空流态不跨 lambda 边界。RunOnUi（DispatcherExtensions 同款三路径）：
        // 无 Application（单测）或已在 UI 线程 → 内联执行；生产 worker → Dispatcher.Invoke 同步 marshal。
        var master = _masterService;
        ((Action)(() => master.Seek(message.Message.FirstFrameTimestampSec))).RunOnUi();
    }
}
