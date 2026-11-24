using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.5.2：<see cref="NmSegment"/> 必须携带证据等级与两个正交的边界标记。
/// <para>
/// 这些字段是「时间条画得对不对」的判据来源，不是装饰：缺了它们，hover 只能说
/// 「重复报文」，说不出「这是实测的还是按 T_REPEAT_MESSAGE 推断的」，
/// 用户无法判断该不该信这一段。
/// </para>
/// </summary>
public class NmSegmentEvidenceTests
{
    [Fact]
    public void Segment_DefaultsToInferredAndUnmarked()
    {
        // 默认值必须是 inferred：M1 的全部现有段都来自时间推断，
        // 老构造点不写这三个参数时语义必须自动正确（spec §7.5.2）。
        var segment = new NmSegment(0, 1, "active");

        Assert.Equal("inferred", segment.Evidence);
        Assert.False(segment.IsWindowTruncated);
        Assert.False(segment.StateBoundaryKnown);
    }

    [Fact]
    public void Segment_CarriesAllThreeFieldsVerbatim()
    {
        var segment = new NmSegment(
            0, 1, "repeat",
            Evidence: "measured",
            IsWindowTruncated: true,
            StateBoundaryKnown: false);

        Assert.Equal("measured", segment.Evidence);
        Assert.True(segment.IsWindowTruncated);
        Assert.False(segment.StateBoundaryKnown);
    }

    [Fact]
    public void Segment_IsWindowTruncatedAndStateBoundaryKnownAreIndependent()
    {
        // spec §7.5.2 明确两者正交：段终点被窗口切断 ≠ 窗口内看不见状态跃迁。
        // 把它们合成一个字段会让「窗口只覆盖单一状态」被误标成截断。
        var truncatedOnly = new NmSegment(0, 1, "normal",
            Evidence: "measured", IsWindowTruncated: true, StateBoundaryKnown: false);
        var boundaryOnly = new NmSegment(0, 1, "normal",
            Evidence: "measured", IsWindowTruncated: false, StateBoundaryKnown: true);

        Assert.True(truncatedOnly.IsWindowTruncated);
        Assert.False(truncatedOnly.StateBoundaryKnown);
        Assert.False(boundaryOnly.IsWindowTruncated);
        Assert.True(boundaryOnly.StateBoundaryKnown);
    }
}
