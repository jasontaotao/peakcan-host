using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>get_dbc_info</c> - returns DBC summary
/// (message count, signal count, node list).
/// </summary>
public sealed class GetDbcInfoTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public GetDbcInfoTool(IChatToolContext context, ILogger<GetDbcInfoTool> logger)
        : base(
            "get_dbc_info",
            "Get a summary of the currently loaded DBC file: number of messages, total signals, and ECU/node list. Returns empty counts when no DBC is loaded.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var info = _context.GetDbcInfo();
        var nodes = new JsonArray();
        foreach (var n in info.Nodes)
            nodes.Add(n);

        var root = new JsonObject
        {
            ["version"] = info.Version,
            ["message_count"] = info.MessageCount,
            ["signal_count"] = info.SignalCount,
            ["nodes"] = nodes,
            ["source_path"] = info.SourcePath,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
