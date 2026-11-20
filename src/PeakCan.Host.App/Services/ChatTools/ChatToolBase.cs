using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Base for the chat tools. Centralizes:
/// <list type="bullet">
///   <item><see cref="Definition"/> construction from a JSON-schema string
///         (parsed once into a <see cref="JsonNode"/>).</item>
///   <item>Uniform error handling: <see cref="ExecuteAsync"/> wraps
///         <see cref="ExecuteCoreAsync"/> so any exception or cancellation
///         surfaces as <c>{"error":..., "hint":...}</c> (never thrown) - per
///         spec §8.1 the <c>hint</c> tells the assistant how to recover.</item>
/// </list>
/// Subclasses implement <see cref="ExecuteCoreAsync"/> and return a JSON
/// string on success.
/// </summary>
public abstract class ChatToolBase : IChatTool
{
    private const string CancelledHint = "请求已取消，可重试或缩小窗口";
    private const string ArgumentHint =
        "检查参数名与取值：参数名需在工具 schema 中，取值需符合类型与范围。";
    // spec §8.1 要求通用异常提示引导先调 get_nm_profile；本基类当前由 NM 工具与
    // 既有工具共用，这里按 spec 原文保留（非 NM 工具也会看到该建议）。
    private const string DataSourceHint =
        "检查数据源是否已加载并包含目标 trace；建议先调用 get_nm_profile 确认 NM 配置。";

    private readonly ChatToolDefinition _definition;

    protected ChatToolBase(
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
            // 调用方主动取消不是故障：不记 warning，返回可重试的 hint。
            return ErrorJson("cancelled", CancelledHint);
        }
        catch (OperationCanceledException ex)
        {
            // token 未取消却抛 OCE（如依赖内部超时）：仍按取消返回，但必须留痕，
            // 不能静默吞掉。
            Logger.LogWarning(ex, "Chat tool {ToolName} was cancelled without a caller request", Name);
            return ErrorJson("cancelled", CancelledHint);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException)
        {
            Logger.LogWarning(ex, "Chat tool {ToolName} rejected its arguments", Name);
            return ErrorJson(ex.Message, ArgumentHint);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Chat tool {ToolName} failed", Name);
            return ErrorJson(ex.Message, DataSourceHint);
        }
    }

    /// <summary>Builds the spec §8.1 error envelope. Serialization handles
    /// escaping so raw exception messages cannot break the JSON.</summary>
    private static string ErrorJson(string error, string hint)
        => JsonSerializer.Serialize(new { error, hint });

    /// <summary>Tool-specific execution. Return a JSON string (success or
    /// <c>{"error":..., "hint":...}</c>). Throwing is safe - the base wraps it.</summary>
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
}
