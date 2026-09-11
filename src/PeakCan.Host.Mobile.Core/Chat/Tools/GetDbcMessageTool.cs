using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>get_dbc_message</c> - one message's definition (id, dlc, sender,
/// signal list).
/// </summary>
public sealed class GetDbcMessageTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"message":{"type":"string","description":"DBC message name"}},"required":["message"],"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public GetDbcMessageTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "get_dbc_message",
            "Get the definition of a single DBC message: CAN id, dlc, sender node, and its signal names. Use to enumerate the signals carried by a message.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var dbc = _context.Dbc;
        if (dbc is null)
            return Task.FromResult("""{"error":"未加载 DBC"}""");

        var args = ParseArgs(argsJson);
        var msgNode = args["message"];
        if (msgNode is null || string.IsNullOrWhiteSpace(msgNode.GetValue<string>()))
            return Task.FromResult("""{"error":"missing 'message'"}""");
        var msgName = msgNode.GetValue<string>();

        var message = dbc.Document.Messages.FirstOrDefault(
            m => m.Name.Equals(msgName, StringComparison.OrdinalIgnoreCase));
        if (message is null)
            return Task.FromResult($$"""{"error":"unknown message: {{msgName}}"}""");

        var root = new JsonObject
        {
            ["message"] = message.Name,
            ["id"] = message.Id & 0x7fffffffu,
            ["id_text"] = (message.Id & 0x80000000u) != 0
                ? (message.Id & 0x7fffffffu).ToString("X8", CultureInfo.InvariantCulture)
                : message.Id.ToString("X3", CultureInfo.InvariantCulture),
            ["is_extended"] = (message.Id & 0x80000000u) != 0,
            ["dlc"] = message.Dlc,
            ["sender"] = message.Sender,
            ["signal_count"] = message.Signals.Count,
            ["signals"] = new JsonArray(message.Signals.Select(s => JsonValue.Create(s.Name)).ToArray()),
        };
        return Task.FromResult(root.ToJsonString());
    }
}
