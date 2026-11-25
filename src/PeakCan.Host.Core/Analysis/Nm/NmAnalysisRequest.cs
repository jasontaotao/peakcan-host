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
    string WindowRef = "full",
    // 已加载的企标 profile；null 表示未选。加载/展平需要文件 IO 与 extends 注册表，
    // 属于调用方职责（NmFlow），NmAnalyzer 保持纯函数。
    NmStandardProfile? Standard = null,
    // 原因字典：null 表示未配置。Note 非空用于报告 Unknowns（加载失败的明确说明）。
    LoadedNmReasonDictionary? Reasons = null,
    string? ReasonDictionaryNote = null);
