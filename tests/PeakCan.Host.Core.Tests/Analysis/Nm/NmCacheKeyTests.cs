using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §10.1：缓存 key 的身份组成。
/// <para>
/// 关键约束（两条互相拉扯，必须同时成立）：
/// </para>
/// <list type="bullet">
/// <item><b>不能重复哈希</b>：DBC 与 sidecar 的内容已被展平进 profile，
/// key 里再拼 DBC hash 会造成「DBC 只改注释也失效」。</item>
/// <item><b>也不能只留 profile</b>：同一 source 重新加载成帧数相同、起止相同的新内容，
/// 会复用旧报告——所以 trace 内容版本必须留在 key 里。</item>
/// </list>
/// </summary>
public class NmCacheKeyTests
{
    private const string Engine = "nm-m2-test";

    private static NmCacheKey Key(
        string sourceId = "src",
        ushort? channel = null,
        double start = 0,
        double end = 10,
        string windowRef = "full",
        int frameCount = 100,
        int traceLoadVersion = 1,
        string canonicalProfileHash = "p1") => new(
            SourceId: sourceId,
            Channel: channel,
            Start: start,
            End: end,
            WindowRef: windowRef,
            FrameCount: frameCount,
            TraceLoadVersion: traceLoadVersion,
            CanonicalProfileHash: canonicalProfileHash,
            EngineVersion: Engine);

    [Fact]
    public void SameFields_AreEqual()
    {
        Assert.Equal(Key(), Key());
    }

    [Fact]
    public void ProfileHashChange_MissesCache()
    {
        // spec §10.1：切 profile 必须重算。
        Assert.NotEqual(Key(canonicalProfileHash: "geely"), Key(canonicalProfileHash: "autosar"));
    }

    [Fact]
    public void TraceLoadVersionChange_MissesCache()
    {
        // spec §10.1：同 source 重载成内容不同的 trace 时必须重算。
        // 帧数/起止都相同，只有 load 版本不同——正是「同帧数不同内容」的场景。
        Assert.NotEqual(Key(traceLoadVersion: 1), Key(traceLoadVersion: 2));
    }

    [Fact]
    public void FrameCountChange_MissesCache()
    {
        Assert.NotEqual(Key(frameCount: 100), Key(frameCount: 101));
    }

    [Fact]
    public void ChannelChange_MissesCache()
    {
        Assert.NotEqual(Key(), Key(channel: 1));
    }

    [Fact]
    public void WindowChange_MissesCache()
    {
        Assert.NotEqual(Key(), Key(start: 1));
        Assert.NotEqual(Key(), Key(end: 11));
        Assert.NotEqual(Key(), Key(windowRef: "viewport"));
    }

    [Fact]
    public void SourceChange_MissesCache()
    {
        Assert.NotEqual(Key(), Key(sourceId: "other"));
    }

    [Fact]
    public void EngineVersionChange_MissesCache()
    {
        var bumped = Key() with { EngineVersion = "nm-m3" };
        Assert.NotEqual(Key(), bumped);
    }

    /// <summary>
    /// spec §10.1「移除 DbcHash」：key 不得再有 DBC 内容哈希字段。
    /// <para>
    /// 用反射钉住字段集合而不是只测行为——字段还在但没参与相等性
    /// （比如被标了 <c>[JsonIgnore]</c> 或改成了非位置属性）同样会漏失效。
    /// </para>
    /// </summary>
    [Fact]
    public void Key_HasNoDbcHashField()
    {
        var names = typeof(NmCacheKey).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("DbcHash", names);
        Assert.DoesNotContain("SupplementHash", names);
        Assert.Contains("CanonicalProfileHash", names);
        Assert.Contains("TraceLoadVersion", names);
    }
}
