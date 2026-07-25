using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Tool <c>get_dbc_signal</c>: look up a single signal's DBC definition
/// (start bit, length, scale, offset, min, max, unit, enum table).
/// </summary>
public sealed class GetDbcSignalTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal":{"type":"string","description":"Signal name"}},"required":["signal"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public GetDbcSignalTool(IChatToolContext context, ILogger<GetDbcSignalTool> logger)
        : base(
            "get_dbc_signal",
            "Look up a single signal's DBC definition: start bit, length, scale (factor), offset, min, max, unit, and enum value table.",
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
        var name = args["signal"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult("""{"error":"missing 'signal'"}""");

        foreach (var msg in dbc.Messages)
        {
            var sig = msg.Signals.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.Ordinal));
            if (sig is null) continue;

            JsonObject? enums = null;
            if (sig.ValueTableName is not null &&
                dbc.ValueTables.TryGetValue(sig.ValueTableName, out var vt))
            {
                enums = new JsonObject();
                foreach (var (k, v) in vt.Entries)
                    enums[k.ToString()] = v;
            }

            var root = new JsonObject
            {
                ["can_id"] = FindRelatedSignalsTool.FormatCanId(msg.Id),
                ["name"] = sig.Name,
                ["start_bit"] = sig.StartBit,
                ["length"] = sig.Length,
                ["scale"] = sig.Factor,
                ["offset"] = sig.Offset,
                ["min"] = sig.Min,
                ["max"] = sig.Max,
                ["unit"] = sig.Unit,
                ["enums"] = enums,
            };
            return Task.FromResult(root.ToJsonString());
        }

        return Task.FromResult($"{{\"error\":\"signal not found: {name.Replace("\"", "\\\"")}\"}}");
    }
}
