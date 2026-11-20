using System.Text.Json.Nodes;
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

    // ---------------- §6.6.2 用户 Timings 逐报文覆盖 ----------------

    [Fact]
    public void Merge_UserTimingsOverridePerMessageButKeepUnsetFields()
    {
        var baseProfile = EmptyProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_A", "A", 8, true,
                    Array.Empty<string>(), 500, 2000),
                new NmMessageDef("0x501", false, "NM_B", "B", 8, true,
                    Array.Empty<string>(), 500, 2000),
            },
        };

        var merged = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(
            Timings: new NmTimings(CycleMs: 250)));

        // 用户只给 CycleMs：逐报文覆盖周期，未给的 TimeoutMs 保持 DBC 值。
        Assert.All(merged.Messages, m => Assert.Equal(250, m.CycleMs));
        Assert.All(merged.Messages, m => Assert.Equal(2000, m.TimeoutMs));
    }

    [Fact]
    public void Merge_UserTimings_DoesNotOverrideNonNmMessages()
    {
        var baseProfile = EmptyProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_A", "A", 8, true,
                    Array.Empty<string>(), 500, 2000),
                new NmMessageDef("0x100", false, "EngineData", "B", 8, false,
                    Array.Empty<string>(), 100, null),
            },
        };

        var merged = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(
            Timings: new NmTimings(CycleMs: 250, TimeoutMs: 750)));

        var nm = Assert.Single(merged.Messages, m => m.IsNm);
        Assert.Equal(250, nm.CycleMs);
        Assert.Equal(750, nm.TimeoutMs);

        // 普通应用报文保持 DBC 值，不被 NM 的用户输入污染。
        var other = Assert.Single(merged.Messages, m => !m.IsNm);
        Assert.Equal(100, other.CycleMs);
        Assert.Null(other.TimeoutMs);
    }

    [Fact]
    public void Merge_ApplyLayoutFalse_KeepsExistingLayoutAndStillMergesTimings()
    {
        var baseProfile = EmptyProfile() with
        {
            Layout = new NmLayout(NidByte: 0, CbvByte: 1),
            Timings = new NmTimings(CycleMs: 500, TimeoutMs: 2000),
        };

        var merged = NmSupplementMerger.Merge(
            baseProfile,
            new NmUserSupplement(NidByte: 5, CbvByte: 6, Timings: new NmTimings(CycleMs: 250)),
            applyLayout: false);

        Assert.Equal((byte?)0, merged.Layout!.NidByte);
        Assert.Equal((byte?)1, merged.Layout.CbvByte);
        Assert.Equal(250, merged.Timings.CycleMs);
    }

    [Fact]
    public void Merge_ApplySelectorsFalse_DoesNotMarkMessagesAndKeepsSelectionMissing()
    {
        var baseProfile = EmptyProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x600", false, "CAN_MSG_A", "ECU_A", 8, false, Array.Empty<string>()),
            },
            MissingFields = new[] { "nm_message_selection" },
        };

        var selectors = new[] { "0x600" };
        var merged = NmSupplementMerger.Merge(
            baseProfile,
            new NmUserSupplement(NmMessageSelectors: selectors),
            applyLayout: false,
            applySelectors: false);

        // stale sidecar 的选择器按旧 DBC 绑定，不能再把报文标成 NM，缺口也不能算已解决。
        Assert.False(Assert.Single(merged.Messages).IsNm);
        Assert.Contains("nm_message_selection", merged.MissingFields);
    }

    // ---------------- §6.6.6 sidecar 健壮性 ----------------

    [Fact]
    public void Sidecar_LoadsSpecSnakeCaseEnumAndAuditFields()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nm-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbcPath = System.IO.Path.Combine(dir, "net.dbc");
            File.WriteAllText(dbcPath, "VERSION \"\"");
            File.WriteAllText(NmSidecarStore.PathFor(dbcPath), """
{
  "version": 2,
  "id_mapping": "low_byte_source_address",
  "nid_byte": 0,
  "created_at": "2026-01-02T03:04:05+00:00",
  "profile_version": 3
}
""");

            var loaded = NmSidecarStore.Load(dbcPath);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Version);
            Assert.Equal(NmIdMappingMode.LowByteSourceAddress, loaded.IdMapping);
            Assert.Equal(3, loaded.ProfileVersion);
            Assert.Equal(2026, loaded.CreatedAt!.Value.Year);

            // 写回时枚举仍是 spec 的 snake_case 字符串，而不是数字。
            NmSidecarStore.Save(dbcPath, loaded);
            var written = JsonNode.Parse(File.ReadAllText(NmSidecarStore.PathFor(dbcPath)))!.AsObject();
            Assert.Equal("low_byte_source_address", written["id_mapping"]!.GetValue<string>());

            // 向导路径会用新的 supplement 重建文件：高版本号不能被默认值 1 降级。
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NidByte: 1));
            var rewritten = NmSidecarStore.Load(dbcPath);
            Assert.NotNull(rewritten);
            Assert.Equal(2, rewritten!.Version);
            Assert.Equal(3, rewritten.ProfileVersion);
            Assert.Equal(loaded.CreatedAt, rewritten.CreatedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sidecar_SaveFillsAuditFieldsAndPreservesExistingCreatedAt()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nm-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbcPath = System.IO.Path.Combine(dir, "net.dbc");
            File.WriteAllText(dbcPath, "VERSION \"\"");

            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NidByte: 0));
            var first = NmSidecarStore.Load(dbcPath);
            Assert.NotNull(first);
            Assert.NotNull(first!.CreatedAt);
            Assert.Equal(1, first.Version);
            Assert.Equal(1, first.ProfileVersion);

            var firstCreatedAt = first.CreatedAt;

            // 第二次保存没有显式 created_at：必须保留文件里原有的值。
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NidByte: 1));
            var second = NmSidecarStore.Load(dbcPath);

            Assert.NotNull(second);
            Assert.Equal((byte?)1, second!.NidByte);
            Assert.Equal(firstCreatedAt, second.CreatedAt);

            var written = JsonNode.Parse(File.ReadAllText(NmSidecarStore.PathFor(dbcPath)))!.AsObject();
            Assert.True(written.ContainsKey("created_at"));
            Assert.True(written.ContainsKey("profile_version"));
            Assert.True(written.ContainsKey("version"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
