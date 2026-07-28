using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>add_to_group</c> - adds signal keys to an
/// existing group.
/// </summary>
public sealed class AddToGroupTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"group_id":{"type":"string","description":"Group ID from create_group."},"signal_keys":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Signal keys in CAN_ID_HEX.SignalName[.SourceId] format."}},"required":["group_id","signal_keys"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public AddToGroupTool(IChatToolContext context, ILogger<AddToGroupTool> logger)
        : base("add_to_group",
               "Add signal keys to an existing group. Returns the count actually added (skips keys already present).",
               DefinitionSchema,
               logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var groupId = args["group_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(groupId))
            return Task.FromResult("""{"error":"missing 'group_id'"}""");

        var keysNode = args["signal_keys"]?.AsArray();
        if (keysNode is null)
            return Task.FromResult("""{"error":"missing 'signal_keys'"}""");

        var keys = new List<string>();
        foreach (var item in keysNode)
        {
            var k = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(k))
                keys.Add(k);
        }

        int added = _context.AddToGroup(groupId, keys);

        var root = new JsonObject
        {
            ["group_id"] = groupId,
            ["added_count"] = added,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
