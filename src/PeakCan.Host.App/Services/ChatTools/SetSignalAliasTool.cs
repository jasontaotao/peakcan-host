using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>set_signal_alias</c> - sets a display alias
/// for a watched signal, replacing the DBC signal name in UI and chat.
/// </summary>
public sealed class SetSignalAliasTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal_key":{"type":"string","description":"Signal key in CAN_ID_HEX.SignalName[.SourceId] format."},"alias":{"type":"string","minLength":1,"description":"Display alias. Pass empty string to clear."}},"required":["signal_key","alias"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public SetSignalAliasTool(IChatToolContext context, ILogger<SetSignalAliasTool> logger)
        : base("set_signal_alias",
               "Set a human-readable alias for a signal. Aliases replace the DBC signal name in the watch list UI and chat display. Pass empty string to clear.",
               DefinitionSchema,
               logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var signalKey = args["signal_key"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(signalKey))
            return Task.FromResult("""{"error":"missing 'signal_key'"}""");

        var alias = args["alias"]?.GetValue<string>() ?? "";
        // Empty string = clear alias.
        _context.SetSignalAlias(signalKey, string.IsNullOrEmpty(alias) ? null : alias);

        var root = new JsonObject
        {
            ["signal_key"] = signalKey,
            ["alias"] = alias,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
