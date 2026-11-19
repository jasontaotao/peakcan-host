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

    private static NmProfile BuildProfile() => new(
        NmProfileSource.Dbc,
        new[]
        {
            new NmMessageDef("0x500", false, "NM", "A", 8, true, SampleReasons, 500, 2000),
            new NmMessageDef("0x600", false, "OTHER", "B", 8, false, Array.Empty<string>()),
        },
        new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
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
}
