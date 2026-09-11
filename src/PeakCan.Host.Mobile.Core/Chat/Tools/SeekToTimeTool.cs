using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>seek_to</c> - seek the trace player to a timestamp (seconds).
/// </summary>
public sealed class SeekToTimeTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"ts":{"type":"number","description":"Timestamp in seconds to seek the trace playback cursor to"}},"required":["ts"],"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public SeekToTimeTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "seek_to",
            "Seek the trace playback cursor to a timestamp in seconds. Use to move the user's view to a specific moment they asked about.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var tsNode = args["ts"];
        if (tsNode is null)
            return Task.FromResult("""{"error":"missing 'ts'"}""");

        double ts;
        try
        {
            ts = tsNode.GetValue<double>();
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult("""{"error":"'ts' must be a number"}""");
        }

        return _context.Seek(ts)
            ? Task.FromResult("""{"status":"ok"}""")
            : Task.FromResult("""{"error":"no source loaded"}""");
    }
}
