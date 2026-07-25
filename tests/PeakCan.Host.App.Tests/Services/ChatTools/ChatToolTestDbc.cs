using PeakCan.Host.Core.Dbc;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

/// <summary>
/// Builds a small in-memory DBC for chat-tool tests. One message (0x182
/// "BMS_Status") with three signals so find_related_signals /
/// get_dbc_signal / get_dbc_message have realistic data to query.
/// </summary>
internal static class ChatToolTestDbc
{
    public const string BmsCanIdHex = "0x182";
    public const uint BmsCanId = 0x182u;

    public static DbcDocument BuildBmsStatusDbc()
    {
        var sigFault = new Signal("BmsFaultState", 0, 4, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1, 0, 0, 15, "", Array.Empty<string>());
        var sigVoltage = new Signal("BatteryVoltage", 4, 16, ByteOrder.LittleEndian, DbcValueType.Unsigned, 0.1, 0, 0, 15, "V", Array.Empty<string>());
        var sigStatus = new Signal("BmsStatus", 20, 4, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1, 0, 0, 15, "", Array.Empty<string>());
        var msg = new Message(BmsCanId, "BMS_Status", 8, "BMS",
            new List<Signal> { sigFault, sigVoltage, sigStatus }, false, null);
        return new DbcDocument(
            "1",
            Array.Empty<Node>(),
            new List<Message> { msg },
            new Dictionary<uint, Message> { [BmsCanId] = msg },
            new Dictionary<string, ValueTable>(),
            "");
    }
}
