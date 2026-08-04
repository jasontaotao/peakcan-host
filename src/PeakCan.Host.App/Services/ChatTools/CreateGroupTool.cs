using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>create_group</c> - creates a signal group for
/// organizing diagnostic findings.
/// </summary>
public sealed class CreateGroupTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"name":{"type":"string","minLength":1,"description":"Group display name, e.g. '欠压分析'."},"signal_keys":{"type":"array","items":{"type":"string"},"description":"Optional initial signal keys in CAN_ID_HEX.SignalName[.SourceId] format."}},"required":["name"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public CreateGroupTool(IChatToolContext context, ILogger<CreateGroupTool> logger)
        : base(
            "create_group",
            "Create a signal group to organize signals by fault scenario or subsystem. Returns the new group_id. Optionally pre-populate with signal keys.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var name = args["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult("""{"error":"missing 'name'"}""");

        var keysNode = args["signal_keys"]?.AsArray();
        var signalKeys = new List<string>();
        if (keysNode is not null)
        {
            foreach (var item in keysNode)
            {
                var k = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(k))
                    signalKeys.Add(k);
            }
        }

        var groupId = _context.CreateGroup(name, signalKeys.Count > 0 ? signalKeys : null);

        var root = new JsonObject
        {
            ["group_id"] = groupId,
            ["name"] = name,
            ["signal_count"] = signalKeys.Count,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
