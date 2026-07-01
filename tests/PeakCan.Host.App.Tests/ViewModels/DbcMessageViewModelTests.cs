using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 15: <see cref="DbcMessageViewModel.From"/> is the pure projection
/// from a parsed <see cref="Message"/> into a row in the DBC tab's
/// <c>DataGrid</c>. The key formatting rule is the ID display:
/// <list type="bullet">
///   <item>Standard (11-bit) → "0x123" (3-hex-digit, no IDE bit)</item>
///   <item>Extended (29-bit, IDE bit set in merged ID) → "0x00000123" (8-hex-digit)</item>
/// </list>
/// </summary>
public class DbcMessageViewModelTests
{
    private static Message MakeMessage(
        uint id = 0x123,
        string name = "Msg",
        byte dlc = 8,
        string sender = "ECU1",
        bool isExtended = false,
        int signalCount = 2)
    {
        // isExtended is informational only — the caller is responsible for
        // setting the IDE bit in `id` so the VM's bit-test sees the right
        // value. We mirror the merge here so the test stays self-contained.
        var mergedId = isExtended ? (id | 0x80000000u) : id;
        var signals = new List<Signal>();
        for (int i = 0; i < signalCount; i++)
        {
            signals.Add(new Signal(
                Name: $"Sig{i}",
                StartBit: (ushort)i,
                Length: 8,
                Order: ByteOrder.LittleEndian,
                ValueType: PeakCan.Host.Core.Dbc.ValueType.Unsigned,
                Factor: 1.0,
                Offset: 0.0,
                Min: 0.0,
                Max: 255.0,
                Unit: "",
                Receivers: Array.Empty<string>()));
        }
        return new Message(mergedId, name, dlc, sender, signals, IsMultiplexed: false, MultiplexorSignalIndex: null);
    }

    [Fact]
    public void From_Populates_All_Fields_From_Source_Message()
    {
        var msg = MakeMessage(id: 0x100, name: "EngineState", dlc: 8, sender: "ECU1", signalCount: 3);

        var vm = DbcMessageViewModel.From(msg);

        vm.Name.Should().Be("EngineState");
        vm.Dlc.Should().Be("8");
        vm.Sender.Should().Be("ECU1");
        vm.SignalCount.Should().Be(3);
        vm.IsExtended.Should().BeFalse();
    }

    [Fact]
    public void From_Standard_Id_Formats_As_3_Hex_Digits()
    {
        // Standard frame: IDE bit clear → 11-bit ID printed as 3 hex digits.
        var msg = MakeMessage(id: 0x123, isExtended: false);

        var vm = DbcMessageViewModel.From(msg);

        vm.Id.Should().Be("0x123");
        vm.IsExtended.Should().BeFalse();
    }

    [Fact]
    public void From_Extended_Id_Formats_As_8_Hex_Digits()
    {
        // Extended frame: IDE bit set → ID masked off the IDE bit then
        // printed as 8 hex digits (matches PEAK MSGBOX / CANalyzer style).
        var msg = MakeMessage(id: 0x123, isExtended: true);

        var vm = DbcMessageViewModel.From(msg);

        vm.Id.Should().Be("0x00000123");
        vm.IsExtended.Should().BeTrue();
    }
}