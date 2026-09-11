using System.Text.Json.Nodes;
using FluentAssertions;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Tests.Chat.Tools;

/// <summary>Shared DBC fixture and execution helper for the chat tool tests.</summary>
public static class ChatToolTestData
{
    public static DbcCatalog CreateCatalog() => DbcCatalog.Parse("""
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ EngineTemp : 16|8@1+ (1,-40) [-40|215] "degC" Vector__XXX

        BO_ 512 OilSystem: 8 ECM
         SG_ OilPressure : 0|8@1+ (10,0) [0|2550] "kPa" Vector__XXX
        """, "engine.dbc").Catalog!;

    public static CachedFrame Frame(long index, double ts, uint canId, byte[]? data = null) =>
        new(index, ts, canId, false, 8, data ?? [0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>Run a tool and parse the returned JSON.</summary>
    public static JsonObject Execute(IChatTool tool, string argsJson = "{}")
    {
        var text = tool.ExecuteAsync(argsJson, CancellationToken.None).GetAwaiter().GetResult();
        var node = JsonNode.Parse(text);
        node.Should().NotBeNull();
        return (node as JsonObject)!;
    }
}
