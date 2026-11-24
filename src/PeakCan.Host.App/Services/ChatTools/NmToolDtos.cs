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

/// <summary>一次 NM 查询的快照：profile + 缓存/新算的 report + 全部已加载帧。</summary>
public sealed record NmToolSnapshot(
    string SourceId,
    ushort? Channel,
    string? DbcPath,
    string? DbcHash,
    NmProfile Profile,
    NmReport Report,
    IReadOnlyList<ReplayFrame> Frames)
{
    /// <summary>
    /// 本次搜索命中的 profile 候选（spec §10.1 选择器）。
    /// <para>
    /// 带进快照而不是让面板再搜一次：重搜会再解析一遍 YAML，
    /// 且在「候选文件刚好被改动」时给出与本次分析不一致的列表。
    /// 默认空表示调用方没有提供候选（测试与工具路径）。
    /// </para>
    /// </summary>
    public IReadOnlyList<NmStandardProfileLocator.Candidate> ProfileCandidates { get; init; }
        = Array.Empty<NmStandardProfileLocator.Candidate>();

    /// <summary>本次 profile 的命中层级；null = 没找到任何 profile。</summary>
    public NmProfileSearchLevel? ProfileLevel { get; init; }
}
