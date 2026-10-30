using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using PeakCan.Host.App.Services.Scripting;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Scripting;

/// <summary>
/// 编辑器页面的生成逻辑。这段 HTML 是字符串拼接进 <c>NavigateToString</c> 的，
/// 因此两件事必须由测试钉住：
/// 1. 初始脚本被**正确转义**——脚本内容里出现 <c>&lt;/script&gt;</c> 时不能提前闭合脚本块；
/// 2. 两条回传路径（input 实时同步 / Ctrl+Enter 运行）真的存在于页面里，
///    否则只改 C# 侧会让编辑器重新变回"只进不出"。
/// </summary>
public sealed class ScriptEditorHtmlTests
{
    // 按行匹配（Multiline）：JSON 字面量本身会包含脚本里的分号，
    // 用 Singleline + 贪婪会把后面整篇 HTML 都吃进来。
    private const string InitialScriptPattern = @"^[ \t]*const initialScript = (.*);[ \t]*$";

    // U+2028/U+2029 是 JS 的行终止符。刻意不用字面量写进源码：
    // C# 会把它们当行分隔符，导致"常量中有换行符"的编译错误。
    private static readonly string ParaSeparator = char.ConvertFromUtf32(0x2028);
    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2029);

    private static string ExtractInitialLiteral(string html)
    {
        var match = Regex.Match(html, InitialScriptPattern, RegexOptions.Multiline);
        match.Success.Should().BeTrue("页面里必须有一段 const initialScript = <json>; 用于回填草稿");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void Initial_Script_Is_Embedded_And_Round_Trips()
    {
        const string script = "var x = 1;\nlog(\"hi\");\npath = 'C:\\\\tmp';";

        var html = ScriptEditorHtml.Build(script);

        JsonSerializer.Deserialize<string>(ExtractInitialLiteral(html))
            .Should().Be(script, "换行、引号、反斜杠都要原样还原，否则用户草稿会变形");
    }

    [Fact]
    public void Script_Content_Cannot_Break_Out_Of_The_Script_Block()
    {
        // 脚本里写 </script> 时，如果直接拼进 HTML，浏览器会提前闭合 <script> 块，
        // 后面的内容变成页面标记（注入）；行终止符未转义也会截断 JS 字符串。
        var hostile = "</script><img src=x onerror=alert(1)><!-- comment end"
                    + ParaSeparator + "a" + LineSeparator + "b";

        var html = ScriptEditorHtml.Build(hostile);
        var literal = ExtractInitialLiteral(html);

        literal.Should().NotContain("<", "尖括号必须被转义，否则脚本块会被提前闭合");
        literal.Should().NotContain(ParaSeparator, "U+2028 必须被转义");
        literal.Should().NotContain(LineSeparator, "U+2029 必须被转义");
        JsonSerializer.Deserialize<string>(literal).Should().Be(hostile, "转义后仍要能还原出原文");
    }

    [Fact]
    public void Empty_Initial_Script_Still_Produces_A_Valid_Page()
    {
        var html = ScriptEditorHtml.Build(null);

        JsonSerializer.Deserialize<string>(ExtractInitialLiteral(html)).Should().BeEmpty();
        html.Should().Contain("createElement('textarea')").And.Contain("postMessage");
    }

    [Fact]
    public void Page_Wires_Both_Text_Sync_And_CtrlEnter_Run()
    {
        var html = ScriptEditorHtml.Build("");

        // 实时同步：input 事件 + text 消息
        html.Should().Contain("addEventListener('input'", "每次改动都要把文本送回 C#");
        html.Should().MatchRegex("type\\s*:\\s*'text'");

        // Ctrl+Enter 运行：把文本随消息一起带上，并阻止插入换行
        html.Should().Contain("ctrlKey");
        html.Should().Contain("preventDefault");
        html.Should().MatchRegex("type\\s*:\\s*'run'");
    }

    [Fact]
    public void Page_Handles_CtrlS_And_CtrlShiftS_For_Save()
    {
        var html = ScriptEditorHtml.Build("");

        // WebView2 是独立 HWND：焦点在编辑器里时窗口级 KeyBinding 收不到按键，
        // 保存必须由页面自己翻译成消息。少了这段，Ctrl+S 在编辑器里就毫无反应。
        html.Should().MatchRegex("type\\s*:\\s*e\\.shiftKey\\s*\\?\\s*'saveAs'\\s*:\\s*'save'");
        html.Should().Contain("'saveAs'");
        html.Should().Contain("'save'");
    }

    [Fact]
    public void Page_Handles_CtrlN_For_New_Script()
    {
        var html = ScriptEditorHtml.Build("");

        // 同 Ctrl+S：窗口级 KeyBinding 收不到编辑器里的按键，新建也只能由页面翻译。
        // 只断言"分支存在"不够：少了 preventDefault，按键会继续走 WebView2 的默认处理；
        // 消息里带上 text，桥会把旧内容又写回 ScriptText，新建等于没新建。
        html.Should().MatchRegex(@"key\s*===\s*'n'[\s\S]{0,300}?preventDefault\(\)");
        html.Should().Contain("post({ type: 'new' });");
        html.Should().NotMatchRegex(@"type:\s*'new'[^}]*text");
    }

    [Fact]
    public void Page_Listens_For_Whole_Text_Replace_From_CSharp()
    {
        var html = ScriptEditorHtml.Build("");

        // 打开文件后必须能把整段文本推回编辑器；没有这条通道，用户下一次键入
        // 会用编辑器里的旧内容覆盖刚打开的文件。
        html.Should().Contain("addEventListener('message'");
        html.Should().Contain("'setText'");
        html.Should().Contain("textarea.value = msg.text");
    }

    [Fact]
    public void SetText_Message_Is_Valid_Json_With_The_Exact_Text()
    {
        // 引号、反斜杠、换行、结束标签都要能原样还原。
        const string text = "a\"b\\c\n</script>";

        var json = ScriptEditorHtml.BuildSetTextMessage(text);

        var doc = JsonDocument.Parse(json).RootElement;
        doc.GetProperty("type").GetString().Should().Be("setText");
        doc.GetProperty("text").GetString().Should().Be(text);
    }
}
