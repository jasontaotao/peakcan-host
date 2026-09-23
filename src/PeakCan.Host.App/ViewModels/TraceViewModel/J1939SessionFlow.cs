using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Core.J1939;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// J1939TP 会话异常事件行（Trace 页签"J1939TP 会话事件"面板）。
/// 纯 DTO：全部 <c>init</c>-only，由 <see cref="TraceViewModel.AddJ1939SessionEvent"/> 一次性构造。
/// </summary>
/// <param name="Timestamp">桥接层 marshal 后由 VM 打墙钟（<see cref="J1939SessionEvent"/> 本身无时间戳）。</param>
/// <param name="Kind">异常种类：PacketLoss / Timeout / Superseded / Evicted。</param>
/// <param name="Detail">会话层给出的补充描述（如 "expected seq 3, got 5"）。</param>
public sealed record J1939SessionEventRow(
    DateTime Timestamp,
    SessionEventKind Kind,
    byte Sa,
    byte Da,
    uint Pgn,
    TpMode Mode,
    string Detail);

public sealed partial class TraceViewModel
{
    /// <summary>会话事件封顶条数；超出裁最旧（防长跑内存无界增长，与 Entries 的 MaxRows 同思路）。</summary>
    internal const int J1939SessionEventsCap = 1000;

    /// <summary>"J1939TP 会话事件"面板展开状态（默认展开；TwoWay 持久化用户折叠）。</summary>
    [ObservableProperty]
    private bool _j1939EventsExpanded = true;

    /// <summary>
    /// 在线 J1939TP 会话异常（丢包/超时/被取代/驱逐）。仅 UI 线程 mutate——
    /// <see cref="Composition.J1939TpSessionEventSink"/> 在 SDK 读线程收到
    /// <see cref="J1939TpLayer.SessionEvent"/> 后经 RunOnUiPost marshal 再调
    /// <see cref="AddJ1939SessionEvent"/>；测试环境无 Application 时 inline 直驱。
    /// 与 <see cref="Entries"/> 相互独立：事件无 CAN ID/通道/帧时间戳，不流过帧过滤、
    /// 按 ID 计数或 DBC 解码。
    /// </summary>
    public ObservableCollection<J1939SessionEventRow> J1939SessionEvents { get; } = new();

    /// <summary>
    /// UI 线程入口：追加一行并按 <see cref="J1939SessionEventsCap"/> 从头部裁最旧。
    /// 非破坏性（只增不覆盖），挂起/过滤不影响事件收集。
    /// </summary>
    internal void AddJ1939SessionEvent(J1939SessionEvent evt)
    {
        J1939SessionEvents.Add(new J1939SessionEventRow(
            DateTime.Now,
            evt.Kind, evt.Sa, evt.Da, evt.Pgn, evt.Mode, evt.Detail));
        while (J1939SessionEvents.Count > J1939SessionEventsCap)
            J1939SessionEvents.RemoveAt(0);
    }
}
