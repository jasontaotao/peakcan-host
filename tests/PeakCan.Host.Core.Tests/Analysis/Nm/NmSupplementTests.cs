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
