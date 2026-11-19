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
    private static readonly byte[] SampleData = [0x01, 0xAB];

    private static NmInputWizardViewModel Wizard(
        IReadOnlyList<ReplayFrame>? frames = null,
        string? dbcPath = DefaultDbcPath) => new(
        new NmToolSnapshot("src", null, dbcPath, "h",
            new NmProfile(NmProfileSource.Dbc,
                new[]
                {
                    new NmMessageDef("0x500", false, "NM", "A", 8, true, SampleReasons, 500, 2000),
                    new NmMessageDef("0x600", false, "OTHER", "B", 8, false, Array.Empty<string>()),
                },
                new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
                Array.Empty<string>(), new NmProvenance()),
            EmptyReport(), frames ?? Array.Empty<ReplayFrame>()));

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
        var w = Wizard(new[] { new ReplayFrame(1.0, 0x500, 2, SampleData, FrameFlags.None) });

        w.SampleByteTexts.Should().Equal("01", "AB");
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
}
