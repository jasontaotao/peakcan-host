// v3.50.2 PATCH (after user screenshot): Δ column showed "—" even
// when BlueLatestValue was set. Root cause: DeltaValue is a computed
// property; WPF DataGrid only re-reads it when PropertyChanged fires
// for "DeltaValue". The BlueLatestValue setter raised PropertyChanged
// for "BlueLatestValue" only — DataGrid never re-evaluated Δ.
//
// Fix: both LatestValue + BlueLatestValue setters raise
// PropertyChanged("DeltaValue") after the value mutation.
//
// This test pins that contract so a future refactor that drops the
// OnPropertyChanged(nameof(DeltaValue)) call gets caught here.
using System.ComponentModel;
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class WatchedSignalRowTests
{
    [Fact]
    public void SettingBlueLatestValue_RaisesPropertyChanged_ForDeltaValue()
    {
        var row = new WatchedSignalRow(
            canIdHex: "0x100",
            messageName: "Msg",
            signalName: "Sig",
            unit: "bit",
            sourceId: null);

        // Prime LatestValue so the Δ formula has a real number on one side.
        row.LatestValue = 10.0;

        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.BlueLatestValue = 12.0;

        raised.Should().Contain(nameof(WatchedSignalRow.DeltaValue),
            "setting BlueLatestValue must raise PropertyChanged(\"DeltaValue\") " +
            "so the watch list Δ column re-binds. Without this, Δ shows \"—\" " +
            "forever even when both anchor and blue-anchor are set.");
        raised.Should().Contain(nameof(WatchedSignalRow.BlueLatestValue));
    }

    [Fact]
    public void SettingLatestValue_RaisesPropertyChanged_ForDeltaValue()
    {
        var row = new WatchedSignalRow(
            canIdHex: "0x100",
            messageName: "Msg",
            signalName: "Sig",
            unit: "bit",
            sourceId: null);

        row.BlueLatestValue = 12.0;

        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.LatestValue = 10.0;

        raised.Should().Contain(nameof(WatchedSignalRow.DeltaValue),
            "setting LatestValue must also raise PropertyChanged(\"DeltaValue\") " +
            "so green-anchor drag refreshes Δ too");
    }

    [Fact]
    public void DeltaValue_Is_NaN_When_Either_Side_Is_NaN()
    {
        var row = new WatchedSignalRow(
            canIdHex: "0x100",
            messageName: "Msg",
            signalName: "Sig",
            unit: "bit",
            sourceId: null);

        // Both NaN initially
        row.DeltaValue.Should().Be(double.NaN);

        row.LatestValue = 5.0;
        row.DeltaValue.Should().Be(double.NaN, "BlueLatestValue still NaN");

        row.BlueLatestValue = 8.0;
        row.DeltaValue.Should().Be(3.0, "8 - 5 = 3");
    }

    [Fact]
    public void DeltaValue_Recomputes_On_Subsequent_Sets()
    {
        var row = new WatchedSignalRow(
            canIdHex: "0x100",
            messageName: "Msg",
            signalName: "Sig",
            unit: "bit",
            sourceId: null)
        { LatestValue = 10.0, BlueLatestValue = 20.0 };

        row.DeltaValue.Should().Be(10.0);

        row.BlueLatestValue = 30.0;
        row.DeltaValue.Should().Be(20.0, "after BlueLatestValue change, Δ re-reads = 30 - 10");

        row.LatestValue = 25.0;
        row.DeltaValue.Should().Be(5.0, "after LatestValue change, Δ re-reads = 30 - 25");
    }
}

// v3.50.5 PATCH: WatchedSignalRow.LatestText — DBC VAL_ table text preferred
// over F2 numeric when Signal+Dbc are set; falls back to F2 numeric when no
// ValueTableName. Sister of v3.50.2 Δ notification tests in the parent class.
public class WatchedSignalRowTextTests
{
    private static readonly string FixturePath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "PeakCan.Host.Core.Tests", "Dbc", "Fixtures",
        "sample-with-val.dbc");

    private static DbcDocument LoadFixture()
    {
        var absPath = System.IO.Path.GetFullPath(FixturePath);
        var text = System.IO.File.ReadAllText(absPath);
        var r = DbcParser.Parse(text);
        if (!r.IsSuccess)
        {
            throw new Xunit.Sdk.XunitException(
                $"Parse failed: code={r.Error!.Code} message={r.Error.Message}");
        }
        return r.Value!;
    }

    [Fact]
    public void LatestText_ReturnsEnumText_WhenMapped()
    {
        var doc = LoadFixture();
        var sig = doc.MessagesById[256].Signals[0]; // SigA, ValueTableName="SigA"
        var row = new WatchedSignalRow("0x100", "MsgA", "SigA", "bit");
        row.Signal = sig;
        row.Dbc = doc;
        row.LatestValue = 2.0; // SigA[2] -> "Two"
        row.LatestText.Should().Be("Two");
    }

    [Fact]
    public void LatestText_ReturnsNumeric_WhenNotMapped()
    {
        // Build a Signal without ValueTableName.
        var sigBare = new Signal(
            Name: "BareSig", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian, ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0, Min: 0, Max: 255, Unit: "",
            Receivers: Array.Empty<string>(), ValueTableName: null);
        var row = new WatchedSignalRow("0x100", "MsgA", "BareSig", "bit");
        row.Signal = sigBare;
        row.LatestValue = 1.23;
        row.LatestText.Should().Be("1.23");
    }
}