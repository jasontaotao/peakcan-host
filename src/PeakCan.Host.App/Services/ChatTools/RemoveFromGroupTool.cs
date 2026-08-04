using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>remove_from_group</c> - removes signal keys
/// from a group.
/// </summary>
public sealed class RemoveFromGroupTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"group_id":{"type":"string","description":"Group ID from create_group."},"signal_keys":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Signal keys to remove from the group."}},"required":["group_id","signal_keys"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public RemoveFromGroupTool(IChatToolContext context, ILogger<RemoveFromGroupTool> logger)
        : base("remove_from_group",
               "Remove signal keys from a group. Returns the count actually removed.",
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

        int removed = _context.RemoveFromGroup(groupId, keys);

        var root = new JsonObject
        {
            ["group_id"] = groupId,
            ["removed_count"] = removed,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
