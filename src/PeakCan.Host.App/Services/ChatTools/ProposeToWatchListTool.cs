using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Tool <c>propose_to_watch_list</c>: add signals to the watch list and
/// synchronously recompute anchor values for the new rows.
/// </summary>
/// <remarks>
/// <b>v12 fix:</b> <see cref="IChatToolContext.AddWatchedSignals"/> now
/// performs Add + RefreshAtAnchor + RefreshAtAnchorBlue inside a single
/// <c>Dispatcher.Invoke</c> to avoid DataGrid ItemContainerGenerator count
/// mismatch. This tool no longer calls RefreshAtAnchor separately.
/// <see cref="GreenLineAnchorFlow.RefreshAtAnchor"/> is a synchronous
/// millisecond-scale method (binary search + decode), so this blocks
/// until the new rows have anchor values - the same round's
/// <c>get_anchor_info</c> reads them immediately. NaN anchors are skipped
/// (no anchor set - new rows keep NaN Latest until the user sets one).
/// </remarks>
public sealed class ProposeToWatchListTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal_keys":{"type":"array","items":{"type":"string"},"description":"Signal keys in CAN_ID_HEX.SignalName format, e.g. 0x182.BmsFaultState"}},"required":["signal_keys"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public ProposeToWatchListTool(IChatToolContext context, ILogger<ProposeToWatchListTool> logger)
        : base(
            "propose_to_watch_list",
            "Add signals to the watch list. Each key is CAN_ID_HEX.SignalName. After adding, anchor values for the new signals are refreshed using the current green/blue anchors so get_anchor_info can read them in the same round.",
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

        var toAdd = new List<WatchedSignalRow>();
        var skipped = new JsonArray();

        foreach (var item in keysNode.AsArray())
        {
            var key = item?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(key))
            {
                skipped.Add(SkipEntry(key, "empty key"));
                continue;
            }
            if (!TryParseSignalKey(key, out var canIdHex, out var signalName, out var sourceId))
            {
                skipped.Add(SkipEntry(key, "invalid key format (expected CAN_ID_HEX.SignalName)"));
                continue;
            }
            if (!FindRelatedSignalsTool.TryParseCanId(canIdHex, out var id) ||
                !dbc.MessagesById.TryGetValue(id, out var msg))
            {
                skipped.Add(SkipEntry(key, "message not found"));
                continue;
            }
            var sig = msg.Signals.FirstOrDefault(s =>
                string.Equals(s.Name, signalName, StringComparison.Ordinal));
            if (sig is null)
            {
                skipped.Add(SkipEntry(key, "signal not found in message"));
                continue;
            }
            if (_context.WatchedSignals.Any(r => r.SignalKey == key))
            {
                skipped.Add(SkipEntry(key, "already in watch list"));
                continue;
            }
            toAdd.Add(new WatchedSignalRow(canIdHex, msg.Name, sig.Name, sig.Unit, sourceId));
        }

        if (toAdd.Count > 0)
        {
            _context.AddWatchedSignals(toAdd);
            // v12: AddWatchedSignals now refreshes anchors inside the same
            // Dispatcher.Invoke to avoid DataGrid ItemContainerGenerator
            // count mismatch (separate Invoke calls let container generation
            // interleave with RefreshAtAnchor INPC notifications).
        }

        var root = new JsonObject
        {
            ["added_count"] = toAdd.Count,
            ["skipped"] = skipped,
        };
        return Task.FromResult(root.ToJsonString());
    }

    private static JsonObject SkipEntry(string key, string reason) => new()
    {
        ["key"] = key,
        ["reason"] = reason,
    };

    /// <summary>Parse "CAN_ID_HEX.SignalName[.SourceId]" into parts.</summary>
    private static bool TryParseSignalKey(string key, out string canIdHex, out string signalName, out string? sourceId)
    {
        canIdHex = "";
        signalName = "";
        sourceId = null;
        var dot1 = key.IndexOf('.');
        if (dot1 <= 0) return false;
        canIdHex = key[..dot1];
        var rest = key[(dot1 + 1)..];
        var dot2 = rest.IndexOf('.');
        if (dot2 < 0)
        {
            signalName = rest;
            return true;
        }
        signalName = rest[..dot2];
        sourceId = rest[(dot2 + 1)..];
        return true;
    }
}
