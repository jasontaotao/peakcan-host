using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Scripting;

/// <summary>
/// 编辑器 → VM 的消息桥。这是 WebView2 之外唯一能单测的一层：
/// 页面里的任何消息都先经过它，再由视图决定要不要执行运行命令。
/// <para>
/// 设计契约：<see cref="ScriptEditorBridge.HandleMessage"/> **返回"请求什么动作"**
/// 而不是自己执行命令——这样测试不需要等异步命令，也不需要碰 WPF。
/// </para>
/// </summary>
public sealed class ScriptEditorBridgeTests
{
    private static ScriptViewModel NewViewModel()
        => new(NullLogger<ScriptViewModel>.Instance,
               new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null));

    [Fact]
    public void Text_Message_Updates_ScriptText_Without_Requesting_Run()
    {
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);

        var request = bridge.HandleMessage("""{"type":"text","text":"var x = 1;"}""");

        request.Should().Be(EditorRequest.None, "编辑内容变化不该触发运行");
        vm.ScriptText.Should().Be("var x = 1;");
    }

    [Fact]
    public void Run_Message_Syncs_Text_Before_Requesting_Run()
    {
        // Ctrl+Enter 的文本随消息一起来，不能依赖前一条 text 消息的到达顺序。
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);
        vm.ScriptText = "old";

        var request = bridge.HandleMessage("""{"type":"run","text":"var x = 2;"}""");

        request.Should().Be(EditorRequest.Run);
        vm.ScriptText.Should().Be("var x = 2;", "运行必须看到编辑器里的最新文本");
    }

    [Theory]
    [InlineData("save", EditorRequest.Save)]
    [InlineData("saveAs", EditorRequest.SaveAs)]
    public void Save_Messages_Request_The_Matching_Command(string type, EditorRequest expected)
    {
        // WebView2 是独立 HWND：焦点在编辑器里时窗口级 KeyBinding 收不到 Ctrl+S，
        // 保存只能靠页面把按键翻译成消息送过来。
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);

        bridge.HandleMessage($$"""{"type":"{{type}}","text":"var x = 3;"}""")
              .Should().Be(expected);

        vm.ScriptText.Should().Be("var x = 3;", "保存前要先把编辑器里的最新文本同步过来");
    }

    [Fact]
    public void New_Message_Requests_A_Blank_Document()
    {
        // Ctrl+N 与 Ctrl+S 同一条通道：焦点在编辑器里时窗口级快捷键收不到。
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);

        bridge.HandleMessage("""{"type":"new"}""").Should().Be(EditorRequest.New);
    }

    [Theory]
    [InlineData(null)]                              // 无消息
    [InlineData("")]                                // 空串
    [InlineData("   ")]
    [InlineData("not json")]                        // 非 JSON
    [InlineData("""{"type":"unknown","text":"x"}""")]
    [InlineData("""{"text":"x"}""")]                // 缺 type
    [InlineData("[1,2]")]                           // 顶层不是对象
    [InlineData("""{"type":"text","text":123}""")]  // text 类型不对 → JsonException
    [InlineData("""{"type":"text","text":{"a":1}}""")]
    public void Malformed_Or_Unknown_Messages_Are_Ignored(string? json)
    {
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);
        vm.ScriptText = "keep";

        var request = bridge.HandleMessage(json);

        request.Should().Be(EditorRequest.None);
        vm.ScriptText.Should().Be("keep", "脏消息不得改写编辑器内容");
    }

    [Fact]
    public void Null_Text_Does_Not_Wipe_Existing_Script()
    {
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);
        vm.ScriptText = "keep";

        bridge.HandleMessage("""{"type":"text","text":null}""");

        vm.ScriptText.Should().Be("keep");
    }

    [Fact]
    public void Text_Supports_Multiline_And_Quotes_RoundTrip()
    {
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);

        bridge.HandleMessage("""{"type":"text","text":"line1\nline2 \"quoted\""}""");

        vm.ScriptText.Should().Be("line1\nline2 \"quoted\"");
    }
}
