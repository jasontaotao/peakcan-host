using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmSupplementTests
{
    private static readonly string[] SelectedMessageIds = ["0x18FFFAF4"];

    private static NmProfile EmptyProfile() => new(
        NmProfileSource.Dbc,
        Array.Empty<NmMessageDef>(),
        new NmTimings(),
        Array.Empty<NmNodeDef>(),
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance());

    [Fact]
    public void Merge_UserLayoutWinsAndSourceBecomesSidecar()
    {
        var merged = NmSupplementMerger.Merge(EmptyProfile(), new NmUserSupplement(
            NidByte: 0, CbvByte: 1, UserDataOffset: 2, UserDataLength: 0));

        Assert.Equal((byte?)0, merged.Layout!.NidByte);
        Assert.Equal((byte?)1, merged.Layout.CbvByte);
        Assert.Equal((byte?)2, merged.Layout.UserDataOffset);
        Assert.Equal(0, merged.Layout.UserDataLength);
        Assert.True((merged.Source & NmProfileSource.Sidecar) != 0);
    }

    [Fact]
    public void Merge_UserMessageSelectorMarksMessageAsNm()
    {
        var baseProfile = EmptyProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
        };

        var merged = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(
            NmMessageSelectors: SelectedMessageIds));

        var msg = Assert.Single(merged.Messages);
        Assert.True(msg.IsNm);
        Assert.Contains("UserSelected", msg.Reasons);
        Assert.False(msg.MarkerAuthoritative);
    }

    [Fact]
    public void Merge_NullSupplement_ReturnsSameProfile()
    {
        var profile = EmptyProfile();
        var merged = NmSupplementMerger.Merge(profile, null);

        Assert.Same(profile, merged);
    }

    [Fact]
    public void Merge_NidByteOnly_KeepsNmMessageSelectionMissing()
    {
        var baseProfile = EmptyProfile() with
        {
            MissingFields = new[] { "dbc", "nm_message_selection" },
        };

        var merged = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(NidByte: 0));

        // 只补了布局字段，报文选择缺口仍然未解决。
        Assert.Contains("nm_message_selection", merged.MissingFields);
        Assert.Contains("dbc", merged.MissingFields);
        Assert.Equal((byte?)0, merged.Layout!.NidByte);
    }

    [Fact]
    public void Merge_SelectorHittingMessage_ClearsNmMessageSelection()
    {
        var baseProfile = EmptyProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
            MissingFields = new[] { "dbc", "nm_message_selection" },
        };

        var merged = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(
            NmMessageSelectors: SelectedMessageIds));

        // 选择器命中报文 -> 选择缺口解决；DBC 缺口无法由补充输入解决，必须保留。
        Assert.DoesNotContain("nm_message_selection", merged.MissingFields);
        Assert.Contains("dbc", merged.MissingFields);
    }

    [Fact]
    public void Merge_TimingsOnly_KeepsLayoutNull()
    {
        var merged = NmSupplementMerger.Merge(EmptyProfile(), new NmUserSupplement(
            Timings: new NmTimings(CycleMs: 500)));

        // 没有任何布局来源时保持 null，而不是物化一个字段全空的 NmLayout。
        Assert.Null(merged.Layout);
        Assert.Equal(500, merged.Timings.CycleMs);
    }

    [Fact]
    public void Sidecar_RoundTripsNextToDbc()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nm-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbcPath = System.IO.Path.Combine(dir, "net.dbc");
            File.WriteAllText(dbcPath, "VERSION \"\"");

            var supplement = new NmUserSupplement(
                NidByte: 0, CbvByte: 1, Timings: new NmTimings(CycleMs: 500));

            NmSidecarStore.Save(dbcPath, supplement);
            var loaded = NmSidecarStore.Load(dbcPath);

            Assert.Equal(System.IO.Path.Combine(dir, "net.nmprofile.json"), NmSidecarStore.PathFor(dbcPath));
            Assert.NotNull(loaded);
            Assert.Equal((byte?)0, loaded!.NidByte);
            Assert.Equal(500, loaded.Timings!.CycleMs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sidecar_MissingFile_ReturnsNull()
    {
        var dbcPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dbc");
        Assert.Null(NmSidecarStore.Load(dbcPath));
    }
}
