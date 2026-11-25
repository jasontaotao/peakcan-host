using System.IO;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class NmInputWizardViewModelTests
{
    private const string DefaultDbcPath = @"C:\nets\net.dbc";

    // CA1861: constant arrays passed as arguments must be static readonly.
    private static readonly string[] SampleReasons = ["x"];
    private static readonly string[] ExistingSelectors = ["0x700"];
    private static readonly string[] UserSelection = ["0x600"];
    private static readonly byte[] SampleData = [0x01, 0xAB];
    private static readonly byte[] FirstCandidateData = [0x11, 0x22, 0x33];
    private static readonly byte[] SecondCandidateData = [0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    private static NmProfile BuildProfile(IReadOnlyList<NmNodeDef>? nodes = null) => new(
        NmProfileSource.Dbc,
        new[]
        {
            new NmMessageDef("0x500", false, "NM", "A", 8, true, SampleReasons, 500, 2000),
            new NmMessageDef("0x600", false, "OTHER", "B", 8, false, Array.Empty<string>()),
        },
        new NmTimings(500, 2000), nodes ?? Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(), new NmProvenance());

    private static NmInputWizardViewModel Wizard(
        IReadOnlyList<ReplayFrame>? frames = null,
        string? dbcPath = DefaultDbcPath)
        => WizardFromProfile(BuildProfile(), frames, dbcPath);

    private static NmInputWizardViewModel WizardFromProfile(
        NmProfile profile,
        IReadOnlyList<ReplayFrame>? frames = null,
        string? dbcPath = DefaultDbcPath) => new(
        new NmToolSnapshot("src", null, dbcPath, "h", profile, EmptyReport(),
            frames ?? Array.Empty<ReplayFrame>()));

    /// <summary>构造一个带指定缺项的报告（DBC 路径为空，避免本机 sidecar 干扰）。</summary>
    private static NmInputWizardViewModel WizardWithMissing(
        NmInputMissing missing,
        NmProfile? profile = null) => new(
        new NmToolSnapshot("src", null, null, "h", profile ?? BuildProfile(),
            EmptyReport() with { MissingInputs = new[] { missing } },
            Array.Empty<ReplayFrame>()));

    private static NmReport EmptyReport() => new(
        new NmWindow(0, 1, "full"), "src", null,
        Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>());

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Validate_RejectsNidEqualToCbv()
    {
        var w = Wizard();
        w.NidByte = 0; w.CbvByte = 0;
        w.Validate().Should().Contain("NID 与 CBV 字节不能相同");
    }

    [Fact]
    public void Validate_RejectsNidBeyondDlc()
    {
        var w = Wizard();
        w.NidByte = 8;
        w.Validate().Should().Contain("NID 字节必须小于 DLC");
    }

    [Fact]
    public void Validate_RejectsUserDataOutOfRange()
    {
        var w = Wizard();
        w.UserDataOffset = 7; w.UserDataLength = 4;
        w.Validate().Should().Contain("用户数据区间超出 DLC");
    }

    [Fact]
    public void Validate_RejectsTimeoutNotGreaterThanCycle()
    {
        var w = Wizard();
        w.CycleMs = 2000; w.TimeoutMs = 2000;
        w.Validate().Should().Contain("超时必须大于周期");
    }

    [Fact]
    public void Validate_AcceptsConsistentInputs()
    {
        var w = Wizard();
        w.NidByte = 0; w.CbvByte = 1; w.UserDataOffset = 2; w.UserDataLength = 0;
        w.CycleMs = 500; w.TimeoutMs = 2000;
        w.Validate().Should().BeEmpty();
    }

    [Fact]
    public void ToSupplement_DoesNotIncludeUntouchedFields()
    {
        var w = Wizard();
        w.NidByte = 0;

        var s = w.ToSupplement();

        s.NidByte.Should().Be(0);
        s.CbvByte.Should().BeNull();
        s.Timings.Should().BeNull();
    }

    [Fact]
    public void SampleByteTexts_FormatsBytesAsUppercaseHex()
    {
        // 候选列表唯一报文 0x600 即默认选中项，样本取自它的帧。
        var w = Wizard(new[] { new ReplayFrame(1.0, 0x600, 2, SampleData, FrameFlags.None) });

        w.SampleByteTexts.Should().Equal("01", "AB");
    }

    [Fact]
    public void SamplePreview_FollowsSelectedCandidate()
    {
        // 两条 DLC 不同的候选：默认取列表第一条，勾选切换后预览与 DLC 必须跟着变。
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[]
            {
                new NmMessageDef("0x601", false, "CAND_A", "A", 3, false, Array.Empty<string>()),
                new NmMessageDef("0x602", false, "CAND_B", "B", 5, false, Array.Empty<string>()),
            },
            new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());
        var frames = new[]
        {
            new ReplayFrame(1.0, 0x601, 3, FirstCandidateData, FrameFlags.None),
            new ReplayFrame(2.0, 0x602, 5, SecondCandidateData, FrameFlags.None),
        };
        var w = WizardFromProfile(profile, frames);

        // 无勾选时取候选列表第一条。
        w.Dlc.Should().Be(3);
        w.SampleByteTexts.Should().Equal("11", "22", "33");

        var second = w.MessageCandidates.Single(c => c.CanIdHex == "0x602");
        second.IsSelected = true;

        w.Dlc.Should().Be(5);
        w.SampleByteTexts.Should().Equal("A1", "A2", "A3", "A4", "A5");
    }

    [Fact]
    public void DbcHashChanged_SkipsLayoutPrefillAndWarns()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            // 快照 DbcHash 为 "h"；sidecar 记录的是旧 DBC。
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(
                NidByte: 0, CbvByte: 1, UserDataOffset: 2, UserDataLength: 0,
                DbcHash: "old-hash"));

            var w = Wizard(dbcPath: dbcPath);

            // 布局类字段来自旧 DBC：不回填，要求用户重新确认。
            w.NidByte.Should().BeNull();
            w.CbvByte.Should().BeNull();
            w.UserDataOffset.Should().BeNull();
            w.UserDataLength.Should().BeNull();
            // 计时仍按既有逻辑预填。
            w.CycleMs.Should().Be(500);
            w.Status.Should().Contain("DBC 已变更");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DbcHashChanged_DoesNotWriteBackOldLayoutOnSave()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(
                NidByte: 0, CbvByte: 1, UserDataOffset: 2, UserDataLength: 0,
                DbcHash: "old-hash"));

            // 不触碰编辑器直接保存：旧 DBC 的布局值不得通过 Pick 的 stored 回落写回。
            var w = Wizard(dbcPath: dbcPath);
            var s = w.ToSupplement();

            s.NidByte.Should().BeNull();
            s.CbvByte.Should().BeNull();
            s.UserDataOffset.Should().BeNull();
            s.UserDataLength.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_PreservesFieldsFromPriorSidecarAcrossTwoSaves()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");

            // 第一轮：只填 NID 并保存。
            var first = Wizard(dbcPath: dbcPath);
            first.NidByte = 0;
            first.SaveCommand.Execute(null);

            // 第二轮：重新打开补 CBV；第一轮的 NID 必须从已有 sidecar 回填且不被覆盖丢失。
            var second = Wizard(dbcPath: dbcPath);
            second.NidByte.Should().Be(0);
            second.CbvByte = 1;
            second.SaveCommand.Execute(null);

            var saved = NmSidecarStore.Load(dbcPath);
            saved.Should().NotBeNull();
            var roundTrip = saved!;
            roundTrip.NidByte.Should().Be(0);
            roundTrip.CbvByte.Should().Be(1);
            // 未触碰的计时字段不得被写成「用户覆盖」。
            roundTrip.Timings.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ToSupplement_AddsCheckedMessageCandidates()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NmMessageSelectors: ExistingSelectors));

            var w = Wizard(dbcPath: dbcPath);
            var candidate = w.MessageCandidates.Single();
            candidate.CanIdHex.Should().Be("0x600");
            candidate.IsSelected.Should().BeFalse();

            candidate.IsSelected = true;

            // 既有选择器与新勾选取并集。
            w.ToSupplement().NmMessageSelectors.Should().BeEquivalentTo("0x700", "0x600");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PrefetchesUserSelectedMessageFromMergedProfile()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NmMessageSelectors: UserSelection));

            // 真实链路：GetNmSnapshotAsync 返回的 profile 已与 sidecar 合并，
            // 0x600 的 IsNm 变为 true 且 Reasons 带 UserSelected。
            var merged = NmSupplementMerger.Merge(BuildProfile(), NmSidecarStore.Load(dbcPath));

            var w = WizardFromProfile(merged, dbcPath: dbcPath);
            var candidate = w.MessageCandidates.Single(c => c.CanIdHex == "0x600");
            candidate.IsSelected.Should().BeTrue();

            // 取消勾选必须从 sidecar 移除，而不是被并集永久保留。
            candidate.IsSelected = false;
            w.ToSupplement().NmMessageSelectors.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- 中间 hex 预览：多帧 + 点击字节 ----

    [Fact]
    public void SampleRows_ShowsUpToThreeFramesOfSameCandidate()
    {
        var frames = new[]
        {
            new ReplayFrame(0.1, 0x600, 2, SampleData, FrameFlags.None),
            new ReplayFrame(0.2, 0x600, 2, SampleData, FrameFlags.None),
            new ReplayFrame(0.3, 0x600, 2, SampleData, FrameFlags.None),
            new ReplayFrame(0.4, 0x600, 2, SampleData, FrameFlags.None),
        };
        var w = Wizard(frames);

        // 同一候选最多取 3 帧，每帧一行、按字节渲染。
        w.SampleRows.Should().HaveCount(3);
        w.SampleRows[0].Bytes.Select(b => b.HexText).Should().Equal("01", "AB");
        w.SampleByteTexts.Should().Equal("01", "AB");
    }

    [Fact]
    public void SelectByte_FirstClickSetsNid_SecondClickOtherByteSetsCbv()
    {
        var w = Wizard(new[] { new ReplayFrame(1.0, 0x600, 2, SampleData, FrameFlags.None) });

        w.SampleRows[0].Bytes.Single(b => b.Index == 1).SelectCommand.Execute(null);

        w.NidByte.Should().Be(1);
        w.CbvByte.Should().BeNull();
        w.SampleRows[0].Bytes.Single(b => b.Index == 1).IsSelected.Should().BeTrue();

        // 换一个字节点：NID 已填，于是落到 CBV（走命令而不是直接调 VM 方法）。
        w.SampleRows[0].Bytes.Single(b => b.Index == 0).SelectCommand.Execute(null);

        w.NidByte.Should().Be(1);
        w.CbvByte.Should().Be(0);
        w.SampleRows[0].Bytes.Single(b => b.Index == 0).IsSelected.Should().BeTrue();
        w.SampleRows[0].Bytes.Single(b => b.Index == 1).IsSelected.Should().BeFalse();
    }

    [Fact]
    public void SelectByte_OutOfRangeDoesNotAssign()
    {
        var w = Wizard(new[] { new ReplayFrame(1.0, 0x600, 2, SampleData, FrameFlags.None) });

        w.SelectByte(8);
        w.SelectByte(-1);

        w.NidByte.Should().BeNull();
        w.CbvByte.Should().BeNull();
        w.SelectedByteIndex.Should().BeNull();
    }

    [Fact]
    public void SelectByte_UpdatesDecodedText()
    {
        var w = Wizard(new[] { new ReplayFrame(1.0, 0x600, 2, SampleData, FrameFlags.None) });

        w.SelectByte(1);

        w.SelectedByteDecode.Should().Contain("byte1 = 0xAB");
        w.SelectedByteDecode.Should().Contain("NID");
    }

    // ---- 采用建议 ----

    [Fact]
    public void MissingItems_ExposeImpactAndStatus_AndRefreshWhenFieldFilled()
    {
        // spec §9.2：左侧缺项必须同时显示「影响」与「状态」。
        var w = WizardWithMissing(new NmInputMissing(
            NmCapability.NidCheck, "nid_byte", "未配置 NID 字节", "建议 byte1", "measured"));

        var item = w.MissingItems.Should().ContainSingle().Subject;
        item.Impact.Should().Contain("NID");
        item.Status.Should().Be("自动可用");

        w.NidByte = 0;

        item.Status.Should().Be("已填写");
    }

    [Fact]
    public void MissingItems_TimingWithoutValue_ShowsNeedInput()
    {
        var profile = BuildProfile() with { Timings = new NmTimings() };
        var w = WizardWithMissing(new NmInputMissing(
            NmCapability.Timing, "cycle_ms", "DBC 没有 NM 周期属性", null, null), profile);

        var item = w.MissingItems.Should().ContainSingle().Subject;
        item.Impact.Should().Contain("周期");
        item.Status.Should().Be("需输入");
    }

    [Fact]
    public void AdoptMissingItemCommand_AppliesSuggestionFromListVm()
    {
        // XAML 的「采用建议」按钮传的是缺项行 VM，命令必须解包到底层 NmInputMissing。
        var w = WizardWithMissing(new NmInputMissing(
            NmCapability.NidCheck, "nid_byte", "未配置 NID 字节",
            "建议 byte1：0xF4", "measured"));
        var item = w.MissingItems.Single();

        w.AdoptMissingItemCommand.Execute(item);

        w.NidByte.Should().Be(1);
        item.Status.Should().Be("已填写");
    }

    [Fact]
    public void AdoptSuggestion_ParsesNidByteSuggestion()
    {
        var w = Wizard();

        w.AdoptSuggestion(new NmInputMissing(
            NmCapability.NidCheck, "nid_byte", "未配置 NID 字节",
            "建议 byte1：byte1 在 10/10 帧中为 0xF4（100%），与站地址一致", "measured"));

        w.NidByte.Should().Be(1);
        w.Status.Should().Contain("已采用建议");
    }

    [Fact]
    public void AdoptSuggestion_ParsesCycleAndTimeoutSuggestions()
    {
        var w = Wizard();

        w.AdoptSuggestion(new NmInputMissing(
            NmCapability.Timing, "cycle_ms", "DBC 没有 NM 周期属性", "500 ms（实测中位）", "measured"));
        w.AdoptSuggestion(new NmInputMissing(
            NmCapability.Timing, "timeout_ms", "DBC 没有 NM 超时属性", "1500 ms（3×cycle 假设）", "assumption"));

        w.CycleMs.Should().Be(500);
        w.TimeoutMs.Should().Be(1500);
    }

    [Fact]
    public void AdoptSuggestion_ParsesUserDataLengthZero()
    {
        var w = Wizard();

        w.AdoptSuggestion(new NmInputMissing(
            NmCapability.UserData, "user_data_range", "用户数据 offset/length 缺失",
            "byte2–byte7 在 10 帧中恒为 0，建议用户数据 length 0（offset 2）", "measured"));

        w.UserDataOffset.Should().Be(2);
        w.UserDataLength.Should().Be(0);
    }

    [Fact]
    public void AdoptSuggestion_UnparseableKeepsValuesAndWarns()
    {
        var w = Wizard();
        w.NidByte = 3;

        w.AdoptSuggestion(new NmInputMissing(
            NmCapability.PnCheck, "pn_enabled", "PN 开关缺失", "不会自动建议 PN 开关", null));

        w.NidByte.Should().Be(3);
        w.Status.Should().Contain("该建议需手动确认");
    }

    // ---- 节点站地址 ----

    [Fact]
    public void NodeAddresses_SaveWritesNodesToSidecar()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            var profile = BuildProfile(new[] { new NmNodeDef("NODE_A", null, null, true) });
            var w = WizardFromProfile(profile, dbcPath: dbcPath);

            var node = w.NodeAddresses.Single();
            node.Name.Should().Be("NODE_A");
            node.StationAddress = 0x42;
            w.SaveCommand.Execute(null);

            var saved = NmSidecarStore.Load(dbcPath);
            saved.Should().NotBeNull();
            saved!.Nodes.Should().ContainSingle()
                .Which.StationAddress.Should().Be(0x42);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_Nodes_SubscribesNewRowsAndRefreshesMissingStatus()
    {
        // 导入会 Clear + 重建 NodeAddresses：新行必须重新订阅，状态也要立即重算。
        var dir = CreateTempDir();
        try
        {
            var importPath = Path.Combine(dir, "nodes.nmprofile.json");
            NmSidecarStore.SaveTo(importPath, new NmUserSupplement(
                Nodes: new[] { new NmNodeDef("NODE_A", 0x21) }));

            var w = WizardWithMissing(new NmInputMissing(
                NmCapability.NidCheck, "nm_node_mapping",
                "没有带站地址的节点，无法比对 NID",
                "在节点表中补充各节点的 StationAddress", "user"));
            var item = w.MissingItems.Single();
            item.Status.Should().Be("需输入");
            w.OpenFilePicker = () => importPath;

            w.ImportProfileCommand.Execute(null);

            item.Status.Should().Be("已填写", "导入的节点已带站地址");
            // 新行订阅生效：清空站地址后状态必须重新变为需输入。
            w.NodeAddresses.Single().StationAddress = null;
            item.Status.Should().Be("需输入");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NodeAddresses_OutOfRangeFailsValidation()
    {
        var profile = BuildProfile(new[] { new NmNodeDef("NODE_A", null) });
        var w = WizardFromProfile(profile);
        w.NodeAddresses.Single().StationAddress = 300;

        w.Validate().Should().Contain(e => e.Contains("站地址必须在 0–255"));
    }

    // ---- 校验补全 ----

    [Fact]
    public void Validate_RejectsNonPositiveCycle()
    {
        var w = Wizard();
        w.CycleMs = 0;
        w.Validate().Should().Contain("周期必须大于 0");
    }

    [Fact]
    public void Validate_RejectsUserDataOffsetAloneBeyondDlc()
    {
        var w = Wizard();
        w.UserDataOffset = 8;
        w.UserDataLength.Should().BeNull();

        w.Validate().Should().Contain("用户数据 offset 必须小于 DLC");
    }

    // ---- 试算展示 ----

    [Fact]
    public async Task Trial_ShowsMessageCycleNidGapBurstCounts()
    {
        // 先 600ms 静默（≥ cycle），再连续 5 个 50ms 短间隔：内容分析器报 burst_observed。
        var stamps = new[] { 0.1, 0.7, 0.75, 0.8, 0.85, 0.9, 0.95 };
        var frames = stamps
            .Select(t => new ReplayFrame(t, 0x500, 8,
                new byte[] { 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                FrameFlags.None))
            .ToArray();
        var w = Wizard(frames);
        w.NidByte = 0;

        await w.TrialCommand.ExecuteAsync(null);

        w.TrialSummary.Should().Contain("识别 1 条报文");
        w.TrialSummary.Should().Contain("周期中位");
        w.TrialSummary.Should().Contain("NID(byte0) 分布");
        w.TrialSummary.Should().Contain("0x11");
        w.TrialSummary.Should().Contain("空档 0 条");
        w.TrialSummary.Should().Contain("观察突发 1 条");
    }

    // ---- sidecar 导出/导入 ----

    [Fact]
    public void ExportImport_RoundTripsEditorConfig()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            var exportPath = Path.Combine(dir, "team.nmprofile.json");

            var source = WizardFromProfile(
                BuildProfile(new[] { new NmNodeDef("NODE_A", null) }), dbcPath: dbcPath);
            source.NidByte = 0;
            source.CbvByte = 1;
            source.CycleMs = 250;
            source.TimeoutMs = 1000;
            source.PnEnabled = true;
            source.NodeAddresses.Single().StationAddress = 0x21;
            source.SaveFilePicker = _ => exportPath;

            source.ExportProfileCommand.Execute(null);

            File.Exists(exportPath).Should().BeTrue();
            source.Status.Should().Contain("已导出");

            // 另一份 DBC 路径没有 sidecar：导入必须回填编辑器且不自动保存。
            var otherDbc = Path.Combine(dir, "other.dbc");
            var target = WizardFromProfile(BuildProfile(), dbcPath: otherDbc);
            target.OpenFilePicker = () => exportPath;

            target.ImportProfileCommand.Execute(null);

            target.NidByte.Should().Be(0);
            target.CbvByte.Should().Be(1);
            target.CycleMs.Should().Be(250);
            target.TimeoutMs.Should().Be(1000);
            target.PnEnabled.Should().BeTrue();
            target.NodeAddresses.Should().ContainSingle()
                .Which.StationAddress.Should().Be(0x21);
            target.Status.Should().Contain("尚未保存");
            // 导入不落盘：other.dbc 的 sidecar 不存在。
            NmSidecarStore.Load(otherDbc).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_DoesNotWriteUntouchedDbcNodes()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            var profile = BuildProfile(new[] { new NmNodeDef("NODE_A", 0x11) });
            var w = WizardFromProfile(profile, dbcPath: dbcPath);
            w.NidByte = 0;

            w.SaveCommand.Execute(null);

            // 纯 DBC 节点不是用户补充输入，保存 NID 不应把它们冻结进 sidecar。
            var saved = NmSidecarStore.Load(dbcPath);
            saved.Should().NotBeNull();
            saved!.NidByte.Should().Be(0);
            saved.Nodes.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_MissingLayoutFieldsDoNotClearExistingValues()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NidByte: 0, CbvByte: 1));
            var importPath = Path.Combine(dir, "team.nmprofile.json");
            NmSidecarStore.SaveTo(importPath,
                new NmUserSupplement(Timings: new NmTimings(CycleMs: 250, TimeoutMs: 1000)));

            var w = Wizard(dbcPath: dbcPath);
            w.NidByte.Should().Be(0);
            w.OpenFilePicker = () => importPath;

            w.ImportProfileCommand.Execute(null);

            // 导入文件没有布局字段：编辑器已确认的 NID/CBV 不得被清空。
            w.NidByte.Should().Be(0);
            w.CbvByte.Should().Be(1);
            w.CycleMs.Should().Be(250);

            w.SaveCommand.Execute(null);
            var saved = NmSidecarStore.Load(dbcPath);
            saved!.NidByte.Should().Be(0);
            saved.CbvByte.Should().Be(1);
            saved.Timings!.CycleMs.Should().Be(250);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExportImport_TransfersNonEditorFields()
    {
        var dir = CreateTempDir();
        try
        {
            var sourceDbc = Path.Combine(dir, "source.dbc");
            var targetDbc = Path.Combine(dir, "target.dbc");
            var exportPath = Path.Combine(dir, "team.nmprofile.json");
            NmSidecarStore.Save(sourceDbc, new NmUserSupplement(
                IdMapping: NmIdMappingMode.SharedId,
                Timings: new NmTimings(CycleMs: 250, TimeoutMs: 1000, RepeatMessageMs: 100),
                Features: new NmFeatures(ActiveWakeupBitEnabled: true),
                ReasonsPath: "reasons.nmreasons.json"));

            var source = Wizard(dbcPath: sourceDbc);
            source.SaveFilePicker = _ => exportPath;
            source.ExportProfileCommand.Execute(null);

            // 目标项目干净：导入后保存必须保留编辑器之外的 IdMapping/计时扩展/feature 标志。
            var target = Wizard(dbcPath: targetDbc);
            target.OpenFilePicker = () => exportPath;
            target.ImportProfileCommand.Execute(null);
            target.SaveCommand.Execute(null);

            var saved = NmSidecarStore.Load(targetDbc);
            saved.Should().NotBeNull();
            saved!.IdMapping.Should().Be(NmIdMappingMode.SharedId);
            saved.Timings!.CycleMs.Should().Be(250);
            saved.Timings.RepeatMessageMs.Should().Be(100);
            saved.Features!.ActiveWakeupBitEnabled.Should().BeTrue();
            // reasons_path 是编辑器之外的字段，导入/导出必须原样透传。
            saved.ReasonsPath.Should().Be("reasons.nmreasons.json");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_ReasonsPathOnly_IsRecognizableAndSaved()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            var importPath = Path.Combine(dir, "reasons-only.nmprofile.json");
            NmSidecarStore.SaveTo(importPath,
                new NmUserSupplement(ReasonsPath: "reasons.nmreasons.json"));

            var w = Wizard(dbcPath: dbcPath);
            w.OpenFilePicker = () => importPath;
            w.ImportProfileCommand.Execute(null);

            // 只有 reasons_path 也是一份可识别的 NM 配置，不能当成空文件拒绝。
            w.Status.Should().NotContain("不包含可识别的 NM 配置");
            w.SaveCommand.Execute(null);

            var saved = NmSidecarStore.Load(dbcPath);
            saved.Should().NotBeNull();
            saved!.ReasonsPath.Should().Be("reasons.nmreasons.json");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Trial_ShowsReasonDictionaryFailure()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath,
                new NmUserSupplement(ReasonsPath: "missing.nmreasons.json"));
            var frames = new[]
            {
                new ReplayFrame(0.1, 0x500, 8,
                    new byte[] { 0x11, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
            };
            var w = Wizard(frames, dbcPath);

            await w.TrialCommand.ExecuteAsync(null);

            // 试算与主分析同口径：字典加载失败必须在试算摘要里可见。
            w.TrialSummary.Should().Contain("原因字典不可用");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_RejectsFileWithoutRecognizableConfig()
    {
        var dir = CreateTempDir();
        try
        {
            var importPath = Path.Combine(dir, "empty.nmprofile.json");
            File.WriteAllText(importPath, "{}");

            var w = Wizard();
            w.NidByte = 3;
            w.OpenFilePicker = () => importPath;

            w.ImportProfileCommand.Execute(null);

            w.Status.Should().Contain("不包含可识别的 NM 配置");
            w.NidByte.Should().Be(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsTimeoutAgainstEffectiveCycleWhenCycleCleared()
    {
        var w = Wizard();
        // 周期框清空后 sidecar 会回落到 DBC/profile 的 500ms，超时 200ms 必须仍然被拒。
        w.CycleMs = null;
        w.TimeoutMs = 200;

        w.Validate().Should().Contain("超时必须大于周期");
    }

    [Fact]
    public void BrowseReasonDictionary_ThenSave_PersistsPath()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            var w = Wizard(dbcPath: dbcPath);
            w.ReasonDictionaryPath.Should().BeNull();
            w.ReasonDictionaryPicker = () => @"C:\logs\bms.nmreasons.json";

            w.BrowseReasonDictionaryCommand.Execute(null);

            w.ReasonDictionaryPath.Should().Be(@"C:\logs\bms.nmreasons.json");
            w.SaveCommand.Execute(null);

            NmSidecarStore.Load(dbcPath)!.ReasonsPath
                .Should().Be(@"C:\logs\bms.nmreasons.json");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ClearReasonDictionary_OverridesExistingSidecarPath()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath,
                new NmUserSupplement(ReasonsPath: "old.nmreasons.json"));

            var w = Wizard(dbcPath: dbcPath);

            w.ReasonDictionaryPath.Should().Be("old.nmreasons.json");
            w.ClearReasonDictionaryCommand.Execute(null);
            w.SaveCommand.Execute(null);

            NmSidecarStore.Load(dbcPath)!.ReasonsPath.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Trial_UsesEditedReasonDictionaryPath()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            var missing = Path.Combine(dir, "missing.nmreasons.json");
            var w = Wizard(dbcPath: dbcPath);
            w.ReasonDictionaryPicker = () => missing;

            w.BrowseReasonDictionaryCommand.Execute(null);
            w.ReasonDictionaryPath.Should().Be(missing);

            await w.TrialCommand.ExecuteAsync(null);

            // 没保存也能在试算里看到刚选字典的加载结果；失败必须显式显示。
            w.TrialSummary.Should().Contain("原因字典不可用");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_PrefillsReasonDictionaryPath()
    {
        var dir = CreateTempDir();
        try
        {
            var profilePath = Path.Combine(dir, "imported.nmprofile.json");
            NmSidecarStore.SaveTo(profilePath,
                new NmUserSupplement(ReasonsPath: "imported.nmreasons.json"));

            var w = Wizard();
            w.OpenFilePicker = () => profilePath;

            w.ImportProfileCommand.Execute(null);

            w.ReasonDictionaryPath.Should().Be("imported.nmreasons.json");
            w.ToSupplement().ReasonsPath.Should().Be("imported.nmreasons.json");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_SecondWithoutReasonsPath_SyncsDisplayToEffectiveValue()
    {
        var dir = CreateTempDir();
        try
        {
            var pathA = Path.Combine(dir, "a.nmprofile.json");
            var pathB = Path.Combine(dir, "b.nmprofile.json");
            NmSidecarStore.SaveTo(pathA,
                new NmUserSupplement(ReasonsPath: "a.nmreasons.json"));
            NmSidecarStore.SaveTo(pathB, new NmUserSupplement(NidByte: 2));

            var w = Wizard();
            w.OpenFilePicker = () => pathA;
            w.ImportProfileCommand.Execute(null);
            w.ReasonDictionaryPath.Should().Be("a.nmreasons.json");

            // 后一次导入不含 reasons_path：显示值必须与生效值一起回落。
            w.OpenFilePicker = () => pathB;
            w.ImportProfileCommand.Execute(null);

            w.ReasonDictionaryPath.Should().BeNull();
            w.ToSupplement().ReasonsPath.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhitespaceReasonsPath_IsNormalizedToNullOnSave()
    {
        var dir = CreateTempDir();
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(ReasonsPath: "   "));

            var w = Wizard(dbcPath: dbcPath);
            w.ToSupplement().ReasonsPath.Should().BeNull();
            w.SaveCommand.Execute(null);

            NmSidecarStore.Load(dbcPath)!.ReasonsPath.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_WhitespaceReasonsPathOnly_IsRejected()
    {
        var dir = CreateTempDir();
        try
        {
            var importPath = Path.Combine(dir, "blank.nmprofile.json");
            NmSidecarStore.SaveTo(importPath,
                new NmUserSupplement(ReasonsPath: "   "));

            var w = Wizard();
            w.OpenFilePicker = () => importPath;

            w.ImportProfileCommand.Execute(null);

            w.Status.Should().Contain("不包含可识别的 NM 配置");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Trial_WithoutDbcPath_ReportsReasonDictionaryCannotResolve()
    {
        var w = Wizard(dbcPath: null);
        w.ReasonDictionaryPath = "x.nmreasons.json";

        await w.TrialCommand.ExecuteAsync(null);

        // 不给 DBC 路径时不能静默跳过：配置了字典就要说清“无法解析”。
        w.TrialSummary.Should().Contain("没有 DBC 路径");
    }
}
