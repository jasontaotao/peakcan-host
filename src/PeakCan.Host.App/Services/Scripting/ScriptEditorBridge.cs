using System.Text.Json;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>页面请求的动作。解析层只负责翻译，不执行命令。</summary>
public enum EditorRequest
{
    /// <summary>纯文本同步，没有动作。</summary>
    None,

    /// <summary>Ctrl+Enter：运行脚本。</summary>
    Run,

    /// <summary>Ctrl+S：保存当前脚本。</summary>
    Save,

    /// <summary>Ctrl+Shift+S：另存为。</summary>
    SaveAs,

    /// <summary>Ctrl+N：新建（空白文档）。</summary>
    New,
}

/// <summary>
/// 脚本编辑器页面回传消息的解析层（纯逻辑，可脱离 WebView2 单测）。
/// <para>
/// 消息协议见 <see cref="ScriptEditorHtml"/>：<c>{type:'text'|'run'|'save'|'saveAs'|'new', text}</c>。
/// 这里只做两件事——把文本写进 <see cref="ScriptViewModel.ScriptText"/>，
/// 以及告诉调用方"这条消息请求什么动作"。**不直接执行命令**：
/// 返回值让视图决定，测试也就无需关心命令的异步时序。
/// </para>
/// <para>
/// 为什么保存也要走这条通道：WebView2 是独立 HWND，焦点在编辑器里时
/// 窗口级 <c>KeyBinding</c> 根本收不到按键。
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

        /// <summary>Ctrl+S：保存当前脚本（文本可省略，以 VM 现有内容为准）。</summary>
        public const string Save = "save";

        /// <summary>Ctrl+Shift+S：另存为。</summary>
        public const string SaveAs = "saveAs";

        /// <summary>Ctrl+N：新建脚本。</summary>
        public const string New = "new";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ScriptViewModel _viewModel;

    public ScriptEditorBridge(ScriptViewModel viewModel)
        => _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    /// <summary>处理一条页面消息。</summary>
    /// <returns>这条消息请求的动作；脏消息一律 <see cref="EditorRequest.None"/>。</returns>
    public EditorRequest HandleMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EditorRequest.None;

        EditorMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<EditorMessage>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return EditorRequest.None;
        }

        if (message?.Type is not { } type) return EditorRequest.None;

        var request = type switch
        {
            MessageTypes.Text => EditorRequest.None,
            MessageTypes.Run => EditorRequest.Run,
            MessageTypes.Save => EditorRequest.Save,
            MessageTypes.SaveAs => EditorRequest.SaveAs,
            MessageTypes.New => EditorRequest.New,
            _ => (EditorRequest?)null,
        };
        if (request is null) return EditorRequest.None;

        // text 为 null 时保留现有草稿：宁可不同步，也不要把用户内容清空。
        if (message.Text is not null)
        {
            _viewModel.ScriptText = message.Text;
        }

        return request.Value;
    }

    private sealed record EditorMessage(string? Type, string? Text);
}
