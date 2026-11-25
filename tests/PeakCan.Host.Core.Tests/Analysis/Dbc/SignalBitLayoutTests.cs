using FluentAssertions;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests.Analysis.Dbc;

public class SignalBitLayoutTests
{
    private static Signal Sig(
        string name, ushort start, byte len,
        ByteOrder order = ByteOrder.LittleEndian,
        bool muxor = false, bool muxed = false, ushort? mux = null)
        => new(name, start, len, order, DbcValueType.Unsigned, 1, 0, 0, 0,
               "", Array.Empty<string>(), muxor, muxed, mux);

    private static Message Msg(byte dlc, params Signal[] signals)
        => new(0x100, "M", dlc, "ECU", signals, false, null);

    private static BitCell Cell(SignalBitLayoutSnapshot s, int byteIndex, int bitIndex)
        => s.Cells.Single(c => c.ByteIndex == byteIndex && c.BitIndex == bitIndex);

    [Fact]
    public void Compute_IntelSignal_MapsBitsFromLsb()
    {
        var snapshot = SignalBitLayout.Compute(Msg(8, Sig("S", 0, 12)));

        for (var bit = 0; bit < 8; bit++)
            Cell(snapshot, 0, bit).OwnerSignal.Should().Be("S");
        for (var bit = 0; bit < 4; bit++)
            Cell(snapshot, 1, bit).OwnerSignal.Should().Be("S");
        Cell(snapshot, 1, 4).OwnerSignal.Should().BeNull();
        snapshot.Segments.Single().InRangeBitCount.Should().Be(12);
        snapshot.Segments.Single().OutOfRangeBitCount.Should().Be(0);
    }

    [Fact]
    public void Compute_MotorolaSignal_MapsStartBitAsMsbZero()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8, Sig("S", 55, 8, ByteOrder.BigEndian)));

        Cell(snapshot, 6, 0).OwnerSignal.Should().Be("S");
        for (var bit = 1; bit <= 7; bit++)
            Cell(snapshot, 7, bit).OwnerSignal.Should().Be("S");
        Cell(snapshot, 7, 0).OwnerSignal.Should().BeNull();
    }

    [Fact]
    public void Compute_PartialOutOfDlc_KeepsInRangeBitsAndAddsDiagnostic()
    {
        var snapshot = SignalBitLayout.Compute(Msg(8, Sig("S", 60, 8)));

        var segment = snapshot.Segments.Single();
        segment.InRangeBitCount.Should().Be(4);
        segment.OutOfRangeBitCount.Should().Be(4);
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "S" && d.Code == "partial_out_of_dlc");
        Cell(snapshot, 7, 4).OwnerSignal.Should().Be("S");
    }

    [Fact]
    public void Compute_LengthZeroAndOver64_AreReportedWithoutThrowing()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8, Sig("Zero", 0, 0), Sig("Long", 0, 65)));

        snapshot.Segments.Should().HaveCount(2);
        snapshot.Segments.Should().OnlyContain(s => s.Cells.Count == 0);
        snapshot.Diagnostics.Should().Contain(d => d.Code == "length_zero");
        snapshot.Diagnostics.Should().Contain(d => d.Code == "length_unsupported");
    }

    [Fact]
    public void Compute_DlcOver64_TruncatesTo512CellsAndWarns()
    {
        var snapshot = SignalBitLayout.Compute(Msg(65, Sig("S", 0, 8)));

        snapshot.DeclaredDlc.Should().Be(65);
        snapshot.RenderedDlc.Should().Be(64);
        snapshot.Cells.Should().HaveCount(512);
        snapshot.Warnings.Should().Contain(w => w.Code == "dlc_truncated");
    }

    [Fact]
    public void Compute_Mux_FiltersOtherBranches_AndKeepsSelector()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 0),
                Sig("B", 16, 8, muxed: true, mux: 1)),
            selectedMuxValue: 1);

        snapshot.EffectiveMuxValue.Should().Be((ushort?)1);
        snapshot.Segments.Select(s => s.SignalName)
            .Should().BeEquivalentTo("Mode", "B");
        Cell(snapshot, 2, 0).OwnerSignal.Should().Be("B");
        Cell(snapshot, 0, 0).OwnerSignal.Should().Be("Mode");
    }

    [Fact]
    public void Compute_Mux_DefaultsToMinimumValue()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 5),
                Sig("B", 16, 8, muxed: true, mux: 3)),
            selectedMuxValue: null);

        snapshot.EffectiveMuxValue.Should().Be((ushort?)3);
        snapshot.Segments.Select(s => s.SignalName)
            .Should().Contain("B").And.NotContain("A");
    }

    [Fact]
    public void Compute_Mux_NoMatchingBranch_WarnsAndKeepsSelector()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 0)),
            selectedMuxValue: 9);

        snapshot.EffectiveMuxValue.Should().Be((ushort?)9);
        snapshot.Warnings.Should().Contain(w => w.Code == "mux_no_match");
        snapshot.Segments.Select(s => s.SignalName)
            .Should().BeEquivalentTo("Mode");
    }

    [Fact]
    public void Compute_Overlap_LastDeclaredWins_AndLoserDiagnostic()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("CellVolt_2", 24, 16),
                Sig("BalanceFlags", 39, 8, ByteOrder.BigEndian)));

        var cell = Cell(snapshot, 4, 0);
        cell.OwnerSignal.Should().Be("BalanceFlags");
        cell.OverlappedSignals.Should().Contain("CellVolt_2");
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "CellVolt_2" && d.Code == "overlap");
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "BalanceFlags" && d.Code == "overlap");
    }

    [Fact]
    public void Compute_BitPositions_AgreeWithSignalDecoder_OnHandWrittenVectors()
    {
        AssertVector(4, 4, ByteOrder.BigEndian, new byte[] { 0x5A }, 10);

        var fd16 = new byte[40];
        fd16[37] = 0x0F;
        AssertVector(300, 16, ByteOrder.BigEndian, fd16, 0xF000);

        var fd8 = new byte[39];
        fd8[37] = 0xF0;
        fd8[38] = 0x0F;
        AssertVector(300, 8, ByteOrder.LittleEndian, fd8, 0xFF);
    }

    private static void AssertVector(
        ushort start, byte length, ByteOrder order, byte[] payload, ulong expected)
    {
        var signal = Sig("S", start, length, order);
        var snapshot = SignalBitLayout.Compute(
            Msg((byte)payload.Length, signal));
        var segment = snapshot.Segments.Single();
        segment.Cells.Should().HaveCount(length);

        ulong assembled = 0;
        var i = 0;
        foreach (var cell in segment.Cells)
        {
            var bit = (ulong)((payload[cell.ByteIndex] >> cell.BitIndex) & 1);
            assembled = order == ByteOrder.LittleEndian
                ? assembled | (bit << i)
                : (assembled << 1) | bit;
            i++;
        }

        SignalDecoder.DecodeRaw(payload, signal).Should().Be(expected);
        assembled.Should().Be(expected);
    }

    [Fact]
    public void Compute_Repeated_IsDeterministic()
    {
        var message = Msg(8, Sig("A", 0, 8), Sig("B", 4, 8));

        var first = SignalBitLayout.Compute(message);
        var second = SignalBitLayout.Compute(message);

        first.Should().BeEquivalentTo(second);
    }

    [Fact]
    public void Compute_LengthOneAtByteBoundary_MapsToNextByte()
    {
        var snapshot = SignalBitLayout.Compute(Msg(8, Sig("S", 8, 1)));

        Cell(snapshot, 1, 0).OwnerSignal.Should().Be("S");
        snapshot.Segments.Single().InRangeBitCount.Should().Be(1);
        snapshot.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Compute_Length64OnFdDlc_RendersEveryBit()
    {
        var snapshot = SignalBitLayout.Compute(Msg(64, Sig("S", 0, 64)));

        snapshot.RenderedDlc.Should().Be(64);
        snapshot.Segments.Single().InRangeBitCount.Should().Be(64);
        snapshot.Segments.Single().Cells.Should().HaveCount(64);
    }

    [Fact]
    public void Compute_AllBitsOutOfDlc_ReportsOutOfDlcWithoutCells()
    {
        var snapshot = SignalBitLayout.Compute(Msg(1, Sig("S", 16, 8)));

        var segment = snapshot.Segments.Single();
        segment.InRangeBitCount.Should().Be(0);
        segment.OutOfRangeBitCount.Should().Be(8);
        segment.Cells.Should().BeEmpty();
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "S" && d.Code == "out_of_dlc");
    }

    [Fact]
    public void Compute_DlcBelow8_RendersDeclaredByteCount()
    {
        var snapshot = SignalBitLayout.Compute(Msg(4, Sig("S", 0, 8)));

        snapshot.RenderedDlc.Should().Be(4);
        snapshot.Cells.Should().HaveCount(32);
    }

    [Fact]
    public void Compute_PaletteIndex_SkipsMuxFilteredSignals()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 0),
                Sig("B", 16, 8, muxed: true, mux: 1)),
            selectedMuxValue: 1);

        snapshot.Segments.Single(s => s.SignalName == "Mode").PaletteIndex.Should().Be(0);
        snapshot.Segments.Single(s => s.SignalName == "B").PaletteIndex.Should().Be(1);
        snapshot.Segments.Should().NotContain(s => s.SignalName == "A");
    }

    [Fact]
    public void DecodeRaw_LengthOver64_StillThrows_AsDecoderContract()
    {
        var signal = Sig("Long", 0, 65);

        var act = () => SignalDecoder.DecodeRaw(new byte[64], signal);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "布局路径不抛异常，但解码器对 >64 位保持抛错，两条路径的差异必须锁定");
    }
}
