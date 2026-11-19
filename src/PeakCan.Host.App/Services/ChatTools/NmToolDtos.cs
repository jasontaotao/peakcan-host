using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>AI 工具与 NM 面板共用的查询参数。</summary>
public sealed record NmToolQuery(
    string? SourceId = null,
    ushort? Channel = null,
    double? TStart = null,
    double? TEnd = null,
    string? WindowRef = null);

/// <summary>一次 NM 查询的快照：profile + 缓存/新算的 report + 已选帧。</summary>
public sealed record NmToolSnapshot(
    string SourceId,
    ushort? Channel,
    string? DbcPath,
    string? DbcHash,
    NmProfile Profile,
    NmReport Report,
    IReadOnlyList<ReplayFrame> Frames);
