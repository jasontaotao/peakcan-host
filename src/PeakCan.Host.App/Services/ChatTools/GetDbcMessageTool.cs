using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Tool <c>get_dbc_message</c>: look up a CAN message's DBC definition
/// (name, dlc, signal list) by CAN ID.
/// </summary>
public sealed class GetDbcMessageTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"can_id_nhex":{"type":"string","description":"CAN ID in 0x-hex, e.g. 0x182"}},"required":["can_id_nhex"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public GetDbcMessageTool(IChatToolContext context, ILogger<GetDbcMessageTool> logger)
        : base(
            "get_dbc_message",
            "Look up a CAN message's DBC definition by CAN ID: name, dlc, and signal list.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var dbc = _context.CurrentDbc;
        if (dbc is null)
            return Task.FromResult("""{"error":"no DBC loaded"}""");

        var args = ParseArgs(argsJson);
        var hex = args["can_id_nhex"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(hex))
            return Task.FromResult("""{"error":"missing 'can_id_nhex'"}""");

        if (!FindRelatedSignalsTool.TryParseCanId(hex, out var id) ||
            !dbc.MessagesById.TryGetValue(id, out var msg))
        {
            return Task.FromResult($"{{\"error\":\"message not found: {hex.Replace("\"", "\\\"")}\"}}");
        }

        var root = new JsonObject
        {
            ["can_id"] = FindRelatedSignalsTool.FormatCanId(msg.Id),
            ["name"] = msg.Name,
            ["dlc"] = msg.Dlc,
            ["signals"] = FindRelatedSignalsTool.SignalsToJsonArray(msg.Signals),
        };
        return Task.FromResult(root.ToJsonString());
    }
}
