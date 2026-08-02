using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class HilStudioProjectionTests
{
    private static readonly ValueTable OffOn = new(
        "M1_SigA_Table",
        new Dictionary<long, string> { [1] = "On", [0] = "Off" });

    private static Signal NewSigA() => new(
        "SigA", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned,
        1, 0, 0, 255, "km/h", Array.Empty<string>(),
        ValueTableName: "M1_SigA_Table");

    private static Signal NewSigB() => new(
        "SigB", 8, 8, ByteOrder.BigEndian, DbcValueType.Signed,
        0.1, -5, -12.8, 12.7, "", Array.Empty<string>());

    [Fact]
    public void Message_Projection_Formats_Id_And_Counts()
    {
        var msg = new Message(0x100, "M1", 8, "ECU1",
            new List<Signal> { NewSigA(), NewSigB() },
            IsMultiplexed: false, MultiplexorSignalIndex: null, Comment: "engine msg");
        var tables = new Dictionary<string, ValueTable> { ["M1_SigA_Table"] = OffOn };

        var row = HilStudioDbcMessageRow.From(msg, tables);

        row.Id.Should().Be("0x100");
        row.Name.Should().Be("M1");
        row.Dlc.Should().Be("8");
        row.Sender.Should().Be("ECU1");
        row.SignalCount.Should().Be(2);
        row.Comment.Should().Be("engine msg");
        row.Signals.Should().HaveCount(2);
        row.Source.Should().BeSameAs(msg);
    }

    [Fact]
    public void Extended_Message_Id_Strips_Ide_Bit_And_Uses_X8()
    {
        var msg = new Message(0x80000123u, "M1", 8, "ECU1",
            new List<Signal>(), IsMultiplexed: false, MultiplexorSignalIndex: null);

        var row = HilStudioDbcMessageRow.From(msg, new Dictionary<string, ValueTable>());

        row.Id.Should().Be("0x00000123");
    }

    [Fact]
    public void Signal_Projection_Formats_BitLayout_Scale_Range()
    {
        var row = HilStudioDbcSignalRow.From(NewSigA(), new Dictionary<string, ValueTable>());

        row.Name.Should().Be("SigA");
        row.BitLayout.Should().Be("0|8@1+");      // LittleEndian -> '1', Unsigned -> '+'
        row.FactorOffset.Should().Be("(1,0)");
        row.MinMax.Should().Be("[0|255]");
        row.Unit.Should().Be("km/h");
    }

    [Fact]
    public void Signed_BigEndian_Signal_Uses_0_And_Minus()
    {
        var row = HilStudioDbcSignalRow.From(NewSigB(), new Dictionary<string, ValueTable>());

        row.BitLayout.Should().Be("8|8@0-");      // BigEndian -> '0', Signed -> '-'
    }

    [Fact]
    public void Signal_Projection_Preserves_Comment()
    {
        var s = new Signal(
            "SigD", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1, 0, 0, 255, "", Array.Empty<string>(), Comment: "speed signal");

        HilStudioDbcSignalRow.From(s, new Dictionary<string, ValueTable>())
            .Comment.Should().Be("speed signal");
    }

    [Fact]
    public void ValueTable_Entries_Expanded_Ordered_By_Key()
    {
        var tables = new Dictionary<string, ValueTable> { ["M1_SigA_Table"] = OffOn };
        var row = HilStudioDbcSignalRow.From(NewSigA(), tables);

        row.ValueTableName.Should().Be("M1_SigA_Table");
        row.ValueTableEntries.Should().HaveCount(2);
        row.ValueTableEntries![0].Key.Should().Be(0);   // 升序, 字典无序需显式排序
        row.ValueTableEntries![0].Label.Should().Be("Off");
        row.ValueTableEntries![1].Key.Should().Be(1);
        row.ValueTableEntries![1].Label.Should().Be("On");
    }

    [Fact]
    public void Signal_Without_ValueTable_Or_With_Dangling_Name_Has_Null_Entries()
    {
        var dangling = new Signal(
            "SigC", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1, 0, 0, 255, "", Array.Empty<string>(),
            ValueTableName: "NoSuchTable");

        HilStudioDbcSignalRow.From(NewSigB(), new Dictionary<string, ValueTable>())
            .ValueTableEntries.Should().BeNull();
        HilStudioDbcSignalRow.From(dangling, new Dictionary<string, ValueTable>())
            .ValueTableEntries.Should().BeNull();
    }
}
