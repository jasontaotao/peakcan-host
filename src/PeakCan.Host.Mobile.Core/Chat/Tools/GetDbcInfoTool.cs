using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>get_dbc_info</c> - summary of the loaded DBC (message/signal
/// counts, node list). Zero counts when no DBC is loaded.
/// </summary>
public sealed class GetDbcInfoTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public GetDbcInfoTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "get_dbc_info",
            "Get a summary of the loaded DBC: whether a DBC is loaded, its source name, total message and signal counts, and the list of node (ECU) names. Call before drilling into a specific signal.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var dbc = _context.Dbc;
        if (dbc is null)
            return Task.FromResult("""{"dbc_loaded":false,"message":"未加载 DBC"}""");

        var document = dbc.Document;
        var signalCount = document.Messages.Sum(m => m.Signals.Count);
        var nodes = new JsonArray(document.Nodes.Select(n => JsonValue.Create(n.Name)).ToArray());
        var root = new JsonObject
        {
            ["dbc_loaded"] = true,
            ["source_name"] = dbc.SourceName,
            ["message_count"] = document.Messages.Count,
            ["signal_count"] = signalCount,
            ["nodes"] = nodes,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
