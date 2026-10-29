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
/// 设计契约：<see cref="ScriptEditorBridge.HandleMessage"/> **返回"是否请求运行"**
/// 而不是自己执行——这样测试不需要等异步 RunAsync，也不需要碰 WPF。
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

        var shouldRun = bridge.HandleMessage("""{"type":"text","text":"var x = 1;"}""");

        shouldRun.Should().BeFalse("编辑内容变化不该触发运行");
        vm.ScriptText.Should().Be("var x = 1;");
    }

    [Fact]
    public void Run_Message_Syncs_Text_Before_Requesting_Run()
    {
        // Ctrl+Enter 的文本随消息一起来，不能依赖前一条 text 消息的到达顺序。
        var vm = NewViewModel();
        var bridge = new ScriptEditorBridge(vm);
        vm.ScriptText = "old";

        var shouldRun = bridge.HandleMessage("""{"type":"run","text":"var x = 2;"}""");

        shouldRun.Should().BeTrue();
        vm.ScriptText.Should().Be("var x = 2;", "运行必须看到编辑器里的最新文本");
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

        var shouldRun = bridge.HandleMessage(json);

        shouldRun.Should().BeFalse();
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
