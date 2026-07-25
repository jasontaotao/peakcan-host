using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One bubble in the AI Chat panel. <see cref="Role"/> selects the
/// DataTemplate: <c>user</c> (right-aligned green bubble),
/// <c>assistant</c> (left-aligned bubble, streaming-capable), or
/// <c>tool_log</c> (collapsible "🔍 执行了 N 个工具" strip).
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public string Role { get; }

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>Tool-call entries for a <c>tool_log</c> bubble. Empty for
    /// user/assistant bubbles.</summary>
    public ObservableCollection<ToolCallEntry> Tools { get; } = new();

    public ChatMessageViewModel(string role, string content = "")
    {
        Role = role;
        _content = content;
    }

    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsToolLog => Role == "tool_log";
}

/// <summary>One executed tool call shown inside a <c>tool_log</c> bubble.</summary>
public sealed class ToolCallEntry
{
    public string Name { get; }
    public string Result { get; set; }

    public ToolCallEntry(string name, string result)
    {
        Name = name;
        Result = result;
    }
}
