using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>search_signals</c> - case-insensitive keyword search over DBC
/// message names and signal names. Results are capped at 50 to keep the
/// tool payload bounded for the phone screen.
/// </summary>
public sealed class SearchSignalsTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"query":{"type":"string","description":"Keyword to match against message or signal names (case-insensitive substring)"}},"required":["query"],"additionalProperties":false}""";

    private const int MaxResults = 50;

    private readonly IMobileChatToolContext _context;

    public SearchSignalsTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "search_signals",
            "Search the loaded DBC for messages/signals whose name contains the given keyword (case-insensitive substring). Returns up to 50 matches. Use to find which messages carry a signal you care about.",
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
        var queryNode = args["query"];
        if (queryNode is null || string.IsNullOrWhiteSpace(queryNode.GetValue<string>()))
            return Task.FromResult("""{"error":"missing 'query'"}""");
        var query = queryNode.GetValue<string>().Trim();

        var results = new List<JsonObject>();
        var truncated = false;
        foreach (var message in dbc.Document.Messages)
        {
            if (results.Count >= MaxResults) { truncated = true; break; }
            if (message.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add(new JsonObject { ["message"] = message.Name, ["signal"] = null });

            foreach (var signal in message.Signals)
            {
                if (results.Count >= MaxResults) { truncated = true; break; }
                if (signal.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(new JsonObject { ["message"] = message.Name, ["signal"] = signal.Name });
            }
        }

        var root = new JsonObject
        {
            ["query"] = query,
            ["count"] = results.Count,
            ["truncated"] = truncated,
            ["results"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()),
        };
        return Task.FromResult(root.ToJsonString());
    }
}
