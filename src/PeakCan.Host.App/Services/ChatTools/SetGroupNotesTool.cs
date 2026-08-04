using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>set_group_notes</c> - attaches analysis
/// conclusions to a signal group.
/// </summary>
public sealed class SetGroupNotesTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"group_id":{"type":"string","description":"Group ID from create_group."},"notes":{"type":"string","description":"Analysis conclusion text."}},"required":["group_id","notes"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public SetGroupNotesTool(IChatToolContext context, ILogger<SetGroupNotesTool> logger)
        : base("set_group_notes",
               "Attach analysis notes/conclusions to a signal group. Use after completing analysis to persist the diagnostic result.",
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

        var notes = args["notes"]?.GetValue<string>();
        if (notes is null)
            return Task.FromResult("""{"error":"missing 'notes'"}""");

        _context.SetGroupNotes(groupId, notes);

        var root = new JsonObject
        {
            ["group_id"] = groupId,
            ["notes_updated"] = true,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
