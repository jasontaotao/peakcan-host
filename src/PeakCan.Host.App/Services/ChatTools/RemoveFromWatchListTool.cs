using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>remove_from_watch_list</c> - removes signals from
/// the watch list by SignalKey and re-runs anchor refresh for remaining rows.
/// </summary>
public sealed class RemoveFromWatchListTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal_keys":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Signal keys to remove in CAN_ID_HEX.SignalName[.SourceId] format. Use the exact key returned by get_anchor_info or search_signals."}},"required":["signal_keys"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public RemoveFromWatchListTool(IChatToolContext context, ILogger<RemoveFromWatchListTool> logger)
        : base(
            "remove_from_watch_list",
            "Remove signals from the watch list by their signal keys. Use when the user wants to focus on fewer signals or correct a mistake.",
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
        var keysNode = args["signal_keys"];
        if (keysNode is null)
            return Task.FromResult("""{"error":"missing 'signal_keys'"}""");

        int removedCount = 0;
        var notFound = new JsonArray();

        foreach (var item in keysNode.AsArray())
        {
            var key = item?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(key))
            {
                notFound.Add(new JsonObject { ["key"] = key });
                continue;
            }
            if (_context.RemoveWatchedSignal(key))
                removedCount++;
            else
                notFound.Add(new JsonObject { ["key"] = key });
        }

        var root = new JsonObject
        {
            ["removed_count"] = removedCount,
            ["not_found"] = notFound,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
