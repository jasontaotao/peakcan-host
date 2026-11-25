using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonCacheTokenTests
{
    [Fact]
    public void NoDictionaryAndNoNote_IsNone()
    {
        Assert.Equal("none", NmReasonCacheToken.For(dictionary: null, loadNote: null));
    }

    [Fact]
    public void LoadFailure_IsDistinctFromNone()
    {
        var token = NmReasonCacheToken.For(
            dictionary: null, loadNote: "原因字典不可用：不存在 C:\\nets\\reasons.json");
        Assert.StartsWith("unavailable:", token, StringComparison.Ordinal);
        Assert.Contains("reasons.json", token, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedDictionary_IncludesHashAndPath()
    {
        var loaded = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
                [new NmReasonFieldDef("S", "0x1", true)]),
            @"C:\nets\reasons.nmreasons.json", "ABCDEF");
        Assert.Equal(@"ABCDEF:C:\nets\reasons.nmreasons.json",
            NmReasonCacheToken.For(loaded, null));
    }

    [Fact]
    public void SameContentDifferentPath_MissesCache()
    {
        var a = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1, [new NmReasonFieldDef("S", "0x1", true)]),
            @"C:\a\reasons.json", "AB");
        var b = a with { Path = @"C:\b\reasons.json" };
        Assert.NotEqual(
            NmReasonCacheToken.For(a, null),
            NmReasonCacheToken.For(b, null));
    }
}
