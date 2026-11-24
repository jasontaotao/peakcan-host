using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReportCacheTests
{
    private static NmReport Report() => new(
        new NmWindow(0, 1, "full"), "src", null,
        Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>(), EngineVersion: NmAnalyzer.EngineVersion);

    /// <summary>
    /// spec §10.1 的 key 身份维度：profile 语义由 <c>CanonicalProfileHash</c> 承载
    /// （原 DbcHash/SupplementHash 已移除），trace 内容版本由 <c>TraceLoadVersion</c> 承载。
    /// </summary>
    private static NmCacheKey Key(
        string canonicalProfileHash = "p1",
        int traceLoadVersion = 1,
        ushort? channel = null,
        int frameCount = 100,
        double start = 0,
        double end = 10) => new(
            "src", channel, start, end, "full", frameCount, traceLoadVersion,
            canonicalProfileHash, NmAnalyzer.EngineVersion);

    [Fact]
    public void PutThenTryGet_ReturnsCachedReport()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        Assert.True(cache.TryGet(Key(), out var report));
        Assert.Equal(NmAnalyzer.EngineVersion, report.EngineVersion);
    }

    [Fact]
    public void DifferentProfileHash_MissesCache()
    {
        // spec §10.1：profile 语义变了（切企标、改时序/规则）必须重算。
        var cache = new NmReportCache();
        cache.Put(Key("profile-a"), Report());

        Assert.False(cache.TryGet(Key("profile-b"), out _));
    }

    [Fact]
    public void DifferentTraceLoadVersion_MissesCache()
    {
        // 同 source 重载成内容不同的 trace：帧数与起止都可以相同，只有代数不同。
        var cache = new NmReportCache();
        cache.Put(Key(traceLoadVersion: 1), Report());

        Assert.False(cache.TryGet(Key(traceLoadVersion: 2), out _));
    }

    [Fact]
    public void EngineVersionBump_Invalidates()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        var bumped = Key() with { EngineVersion = "nm-m1-2" };
        Assert.False(cache.TryGet(bumped, out _));
    }

    [Fact]
    public void InvalidateSource_DropsOnlyThatSource()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());
        cache.Put(Key() with { SourceId = "other" }, Report());

        cache.Invalidate("src");

        Assert.False(cache.TryGet(Key(), out _));
        Assert.True(cache.TryGet(
            Key() with { SourceId = "other" }, out _));
    }

    [Fact]
    public void InvalidateAll_ClearsEveryEntry()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());
        cache.Put(Key() with { SourceId = "other" }, Report());

        cache.Invalidate();

        Assert.False(cache.TryGet(Key(), out _));
        Assert.False(cache.TryGet(
            Key() with { SourceId = "other" }, out _));
    }

    [Fact]
    public void ChannelFrameCountAndWindow_ArePartOfIdentity()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        Assert.False(cache.TryGet(Key(channel: 1), out _));
        Assert.False(cache.TryGet(Key(frameCount: 101), out _));
        Assert.False(cache.TryGet(Key(start: 1), out _));
        Assert.False(cache.TryGet(Key(end: 11), out _));
    }

    [Fact]
    public void ConcurrentPutGetInvalidate_RemainsUsable()
    {
        var cache = new NmReportCache();
        NmCacheKey[] keys =
        [
            Key(), Key(channel: 1), Key(channel: 2),
            Key() with { SourceId = "other" },
        ];

        Parallel.For(0, 2000, i =>
        {
            var key = keys[i % keys.Length];
            switch (i % 4)
            {
                case 0:
                    cache.Put(key, Report());
                    break;
                case 1:
                    cache.Invalidate(key.SourceId);
                    break;
                case 2:
                    cache.Invalidate();
                    break;
                default:
                    _ = cache.TryGet(key, out _);
                    break;
            }
        });

        cache.Put(Key(), Report());
        Assert.True(cache.TryGet(Key(), out _));
    }
}
