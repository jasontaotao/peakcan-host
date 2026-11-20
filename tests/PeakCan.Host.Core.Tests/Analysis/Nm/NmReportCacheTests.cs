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

    private static NmCacheKey Key(
        string dbcHash = "d1",
        string supplement = "s1",
        ushort? channel = null,
        int frameCount = 100,
        double start = 0,
        double end = 10) => new(
            "src", channel, frameCount, dbcHash, supplement, start, end, NmAnalyzer.EngineVersion);

    [Fact]
    public void PutThenTryGet_ReturnsCachedReport()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        Assert.True(cache.TryGet(Key(), out var report));
        Assert.Equal(NmAnalyzer.EngineVersion, report.EngineVersion);
    }

    [Fact]
    public void DifferentDbcHash_MissesCache()
    {
        var cache = new NmReportCache();
        cache.Put(Key("dbc-a"), Report());

        Assert.False(cache.TryGet(Key("dbc-b"), out _));
    }

    [Fact]
    public void DifferentSupplementHash_MissesCache()
    {
        var cache = new NmReportCache();
        cache.Put(Key(supplement: "sidecar-a"), Report());

        Assert.False(cache.TryGet(Key(supplement: "sidecar-b"), out _));
    }

    [Fact]
    public void EngineVersionBump_Invalidates()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        var bumped = new NmCacheKey("src", null, 100, "d1", "s1", 0, 10, "nm-m1-2");
        Assert.False(cache.TryGet(bumped, out _));
    }

    [Fact]
    public void InvalidateSource_DropsOnlyThatSource()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());
        cache.Put(new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion), Report());

        cache.Invalidate("src");

        Assert.False(cache.TryGet(Key(), out _));
        Assert.True(cache.TryGet(
            new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion), out _));
    }

    [Fact]
    public void InvalidateAll_ClearsEveryEntry()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());
        cache.Put(new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion), Report());

        cache.Invalidate();

        Assert.False(cache.TryGet(Key(), out _));
        Assert.False(cache.TryGet(
            new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion), out _));
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
            new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion),
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
