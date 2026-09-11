using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Base for the mobile chat tools. Centralizes:
/// <list type="bullet">
///   <item><see cref="Definition"/> construction from a JSON-schema string
///         (parsed once into a <see cref="JsonNode"/>).</item>
///   <item>Uniform error handling: <see cref="ExecuteAsync"/> wraps
///         <see cref="ExecuteCoreAsync"/> so any exception or cancellation
///         surfaces as <c>{"error":"..."}</c> (never thrown) - the assistant
///         can react in the next round.</item>
/// </list>
/// Subclasses implement <see cref="ExecuteCoreAsync"/> and return a JSON
/// string on success. Port of the desktop <c>ChatToolBase</c>.
/// </summary>
public abstract class MobileChatToolBase : IChatTool
{
    private readonly ChatToolDefinition _definition;

    protected MobileChatToolBase(
        string name,
        string description,
        string parametersJsonSchema,
        ILogger logger)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _definition = new ChatToolDefinition(
            Name,
            description,
            JsonNode.Parse(parametersJsonSchema)
                ?? throw new ArgumentException("parametersJsonSchema parsed to null", nameof(parametersJsonSchema)));
    }

    public string Name { get; }

    public ChatToolDefinition Definition => _definition;

    protected ILogger Logger { get; }

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct)
    {
        try
        {
            return await ExecuteCoreAsync(argsJson, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "{\"error\":\"cancelled\"}";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Chat tool {ToolName} failed", Name);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Tool-specific execution. Return a JSON string (success or
    /// <c>{"error":"..."}</c>). Throwing is safe - the base wraps it.</summary>
    protected abstract Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct);

    /// <summary>Parse the raw arguments JSON into a <see cref="JsonObject"/>
    /// for keyed access. Returns an empty object if <paramref name="argsJson"/>
    /// is null/blank.</summary>
    protected static JsonObject ParseArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new JsonObject();
        return JsonNode.Parse(argsJson) as JsonObject
            ?? new JsonObject();
    }

    /// <summary>JSON has no NaN; emit null for an unset/NaN timestamp.</summary>
    protected static JsonNode? NanOrNull(double v) => double.IsNaN(v) ? null : JsonValue.Create(v);

    /// <summary>Format a timestamp for labels (F4 seconds), matching the
    /// system-prompt time convention.</summary>
    protected static string TsLabel(double ts)
        => double.IsNaN(ts) ? "" : ts.ToString("F4", CultureInfo.InvariantCulture);
}
