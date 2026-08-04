using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Tool <c>find_related_signals</c>: look up the DBC for other signals in
/// the same CAN message as a target (by CAN ID or by signal name). Only
/// reads DBC definitions - does NOT scan trace data.
/// </summary>
public sealed class FindRelatedSignalsTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"target":{"type":"string","description":"CAN ID (0x-hex like 0x182 or decimal) OR a signal name; returns every signal in the same message"}},"required":["target"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public FindRelatedSignalsTool(IChatToolContext context, ILogger<FindRelatedSignalsTool> logger)
        : base(
            "find_related_signals",
            "Find related signals in the same CAN message by CAN ID or signal name. Reads DBC definitions only - does not scan trace data.",
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
        var target = args["target"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(target))
            return Task.FromResult("""{"error":"missing 'target'"}""");

        Message? msg = null;
        if (TryParseCanId(target, out var id) && dbc.MessagesById.TryGetValue(id, out var byId))
        {
            msg = byId;
        }
        else
        {
            // Treat as signal name: find the message that contains it.
            msg = dbc.Messages.FirstOrDefault(m =>
                m.Signals.Any(s => string.Equals(s.Name, target, StringComparison.Ordinal)));
        }

        if (msg is null)
            return Task.FromResult($"{{\"error\":\"not found: {Escape(target)}\"}}");

        var root = new JsonObject
        {
            ["can_id"] = FormatCanId(msg.Id),
            ["name"] = msg.Name,
            ["signal_count"] = msg.Signals.Count,
            ["signals"] = SignalsToJsonArray(msg.Signals),
        };
        return Task.FromResult(root.ToJsonString());
    }

    /// <summary>Parse a CAN ID string: <c>0x</c>-prefixed hex or decimal.
    /// Returns false for non-numeric strings (treated as signal names by
    /// the caller).</summary>
    internal static bool TryParseCanId(string target, out uint id)
    {
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(target.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        if (uint.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            return true;
        id = 0;
        return false;
    }

    /// <summary>Format a CAN ID (with IDE bit stripped) as <c>0x</c>-hex.</summary>
    internal static string FormatCanId(uint id) => "0x" + (id & 0x7FFFFFFFu).ToString("X");

    /// <summary>Shared signal-list serializer (name + start_bit + length +
    /// factor + offset + min + max + unit + comment). v12: added
    /// factor/offset/min/max/comment per spec Step 3b.</summary>
    internal static JsonArray SignalsToJsonArray(IReadOnlyList<Signal> signals)
    {
        var arr = new JsonArray();
        foreach (var s in signals)
        {
            arr.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["start_bit"] = s.StartBit,
                ["length"] = s.Length,
                ["factor"] = s.Factor,
                ["offset"] = s.Offset,
                ["min"] = s.Min,
                ["max"] = s.Max,
                ["unit"] = s.Unit,
                ["comment"] = s.Comment,
            });
        }
        return arr;
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"");
}
