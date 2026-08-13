using System.Collections.ObjectModel;
using System.ComponentModel;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.x (会话状态剥离 Task 1): Trace 会话级状态的唯一归属（session state home）。
/// 承载 4 组会话状态（<see cref="WatchedSignals"/> / <see cref="SignalGroups"/>
/// / <see cref="MasterSourceId"/> / <see cref="GlobalCanIdFilter"/>）+ 从
/// <c>.tmtrace</c> 文件恢复会话（<see cref="OpenSessionAsync"/>）+ 生成快照
/// （<see cref="BuildSnapshot"/>）。窗口级状态（viewport / scrubber / speed /
/// loop）留在 TraceViewerViewModel，本接口只暴露会话持久化边界。
/// <para>
/// 注册为 singleton（Task 2 起由 AppShellViewModel / TraceViewerViewModel
/// 消费；Task 3 起 VM 经 <see cref="PropertyChanged"/> 透传会话级状态变更）。
/// 继承 <see cref="INotifyPropertyChanged"/>，使 MasterSourceId /
/// GlobalCanIdFilter 的变更可被 VM 订阅转发。
/// </para>
/// </summary>
public interface ITraceSessionService : INotifyPropertyChanged
{
    /// <summary>watch 列表行（占位行由 BuildSnapshot 过滤，不进 bundle）。</summary>
    ObservableCollection<WatchedSignalRow> WatchedSignals { get; }

    /// <summary>信号分组（v12 Step 7 持久化形状，与 watch 列表相互独立）。</summary>
    ObservableCollection<WatchedSignalGroup> SignalGroups { get; }

    /// <summary>master source 的 SourceId；null = 未设置。</summary>
    string? MasterSourceId { get; set; }

    /// <summary>全局 CAN-ID 过滤器文本（空串 = 不过滤）。</summary>
    string GlobalCanIdFilter { get; set; }

    /// <summary>
    /// Load a <c>.tmtrace</c> bundle and apply its session data to the
    /// registry. Returns the list of source .asc paths that did NOT
    /// resolve (missing / unloadable) — empty when every source loaded.
    /// </summary>
    Task<IReadOnlyList<string>> OpenSessionAsync(string path);

    /// <summary>
    /// Collect the current session state into a
    /// <see cref="TraceSessionBundleDto"/>. Path-reference only for
    /// .asc recordings; window-level playback state is replaced with
    /// defaults (this service does not own the transport cursor).
    /// </summary>
    TraceSessionBundleDto BuildSnapshot();

    /// <summary>True when the registry currently holds at least one source.</summary>
    bool HasContent { get; }
}
