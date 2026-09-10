using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Models;

public class FrameRowTests
{
    private static readonly byte[] Data = [1, 2, 3];
    private const string EngineDbc = """
        VERSION ""
        NS_ :
        BS_:
        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
        """;

    [Fact]
    public void DisplayTexts_AreCached()
    {
        var row = new FrameRow(1.25, 0x103, false, 3, Data);

        row.TimeText.Should().BeSameAs(row.TimeText);
        row.IdText.Should().BeSameAs(row.IdText);
        row.DataText.Should().BeSameAs(row.DataText);
        row.TimeText.Should().Be("1.250000");
        row.IdText.Should().Be("103");
        row.DataText.Should().Be("01 02 03");
    }

    [Fact]
    public void ExtendedId_UsesEightHexDigits()
    {
        var row = new FrameRow(0, 0x18ff0103, true, 0, Data);
        row.IdText.Should().Be("18FF0103");
    }

    [Fact]
    public void FromReplayFrame_Decodes_Visible_Dbc_Signals()
    {
        var catalog = DbcCatalog.Parse(EngineDbc).Catalog!;
        var frame = new ReplayFrame(0, 0x100, 2, [0x01, 0x02], default, false);

        var row = FrameRow.FromReplayFrame(frame, catalog);

        row.SignalSummaryText.Should().Contain("EngineSpeed=128.25rpm");
    }

    [Fact]
    public void FromCached_Without_Dbc_Keeps_Empty_Signal_Summary()
    {
        var frame = new CachedFrame(0, 1, 0x100, false, 2, [1, 2]);

        var row = FrameRow.FromCached(frame);

        row.SignalSummaryText.Should().BeEmpty();
    }

    [Fact]
    public void FromCached_Decodes_When_Dbc_IsSupplied()
    {
        var catalog = DbcCatalog.Parse(EngineDbc, "engine.dbc").Catalog!;
        var frame = new CachedFrame(7, 1, 0x100, false, 2, [0x01, 0x02]);

        var row = FrameRow.FromCached(frame, catalog);

        row.SignalSummaryText.Should().Be("EngineSpeed=128.25rpm");
    }

    [Fact]
    public void FromReplayFrame_Unknown_CanId_Keeps_Empty_Signal_Summary()
    {
        var catalog = DbcCatalog.Parse(EngineDbc).Catalog!;
        var frame = new ReplayFrame(0, 0x101, 2, [0x01, 0x02], default, false);

        var row = FrameRow.FromReplayFrame(frame, catalog);

        row.SignalSummaryText.Should().BeEmpty();
    }

    [Fact]
    public void PgnSaText_ExtendedFrame_FormatsPgnAndSa()
    {
        // 0x18F00411：EDP/DP=0、PF=0xF0（PDU2）、PS=0x04、SA=0x11
        var row = new FrameRow(0, 0x18F00411, true, 8, Data);

        row.PgnSaText.Should().Be("F004·11");
    }

    [Fact]
    public void PgnSaText_StandardFrame_Empty()
    {
        var row = new FrameRow(0, 0x123, false, 8, Data);

        row.PgnSaText.Should().BeEmpty();
    }

    [Fact]
    public void PgnSaText_Pdu1_PgnDropsPs()
    {
        // PDU1（PF=0xEA < 0xF0）：PS 是目标地址，不属于 PGN → PGN 低 8 位为 0
        var id = PeakCan.Host.Core.J1939.J1939Id.Compose(6, 0xEA00, 0x30, 0x42);
        var row = new FrameRow(0, id, true, 8, Data);

        row.PgnSaText.Should().Be("EA00·30");
    }

    [Fact]
    public void PgnSaText_FromCached_SameResultAsReplay()
    {
        var frame = new CachedFrame(0, 0, 0x18F00411, true, 8, Data);
        var replay = new ReplayFrame(0, 0x18F00411, 8, Data, default, true);

        FrameRow.FromCached(frame).PgnSaText.Should().Be(
            FrameRow.FromReplayFrame(replay).PgnSaText);
    }
}
