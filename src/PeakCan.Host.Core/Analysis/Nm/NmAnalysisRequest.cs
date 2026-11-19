using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 一次分析所需的全部输入快照。调用方必须在 UI 线程取好快照（帧列表引用、
/// DBC、profile、窗口），再交给后台线程。
/// </summary>
public sealed record NmAnalysisRequest(
    string SourceId,
    ushort? Channel,
    IReadOnlyList<ReplayFrame> Frames,
    PeakCan.HIL.Core.Dbc.DbcDocument? Dbc,
    NmProfile Profile,
    double Start,
    double End,
    string WindowRef = "full");
