using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Tool <c>seek_to</c>: seek the master trace source to a timestamp.
/// </summary>
public sealed class SeekToTimeTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"ts":{"type":"number","description":"Timestamp in seconds to seek the trace cursor to"}},"required":["ts"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public SeekToTimeTool(IChatToolContext context, ILogger<SeekToTimeTool> logger)
        : base(
            "seek_to",
            "Seek the master trace source playback cursor to a timestamp (seconds).",
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
            : Task.FromResult("""{"error":"no master source loaded"}""");
    }
}
