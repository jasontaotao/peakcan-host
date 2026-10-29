using System.Text.Json;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// 脚本编辑器页面回传消息的解析层（纯逻辑，可脱离 WebView2 单测）。
/// <para>
/// 消息协议见 <see cref="ScriptEditorHtml"/>：<c>{type:'text'|'run', text}</c>。
/// 这里只做两件事——把文本写进 <see cref="ScriptViewModel.ScriptText"/>，
/// 以及告诉调用方"这条消息是否请求运行"。**不直接执行命令**：
/// 返回值让视图决定，测试也就无需关心 <c>RunAsync</c> 的异步时序。
/// </para>
/// <para>
/// 容错：页面上的任何脏消息（非 JSON、缺字段、未知类型、非字符串）都只被忽略，
/// 不抛异常、也不改已有草稿——编辑器不该因为一条坏消息而丢内容。
/// </para>
/// </summary>
public sealed class ScriptEditorBridge
{
    /// <summary>页面 → C# 的消息类型。</summary>
    public static class MessageTypes
    {
        /// <summary>编辑内容变化（实时同步）。</summary>
        public const string Text = "text";

        /// <summary>Ctrl+Enter：用附带的文本运行脚本。</summary>
        public const string Run = "run";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ScriptViewModel _viewModel;

    public ScriptEditorBridge(ScriptViewModel viewModel)
        => _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    /// <summary>处理一条页面消息。</summary>
    /// <returns><c>true</c> 表示这条消息请求运行脚本。</returns>
    public bool HandleMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        EditorMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<EditorMessage>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (message?.Type is not { } type) return false;
        if (type is not (MessageTypes.Text or MessageTypes.Run)) return false;

        // text 为 null 时保留现有草稿：宁可不同步，也不要把用户内容清空。
        if (message.Text is not null)
        {
            _viewModel.ScriptText = message.Text;
        }

        return type == MessageTypes.Run;
    }

    private sealed record EditorMessage(string? Type, string? Text);
}
