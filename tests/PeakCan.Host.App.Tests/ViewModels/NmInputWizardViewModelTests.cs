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
    // CA1861: constant arrays passed as arguments must be static readonly.
    private static readonly string[] SampleReasons = ["x"];
    private static readonly byte[] SampleData = [0x01, 0xAB];

    private static NmInputWizardViewModel Wizard(IReadOnlyList<ReplayFrame>? frames = null) => new(
        new NmToolSnapshot("src", null, @"C:\nets\net.dbc", "h",
            new NmProfile(NmProfileSource.Dbc,
                new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, SampleReasons, 500, 2000) },
                new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
                Array.Empty<string>(), new NmProvenance()),
            EmptyReport(), frames ?? Array.Empty<ReplayFrame>()));

    private static NmReport EmptyReport() => new(
        new NmWindow(0, 1, "full"), "src", null,
        Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>());

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
}
