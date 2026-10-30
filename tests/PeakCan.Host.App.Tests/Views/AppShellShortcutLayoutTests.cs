using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// P1 全局快捷键的**声明层**契约。
/// <para>
/// 这组断言的真正目的是封杀"写了但没接"这一类 bug：本轮之前
/// <c>Ctrl+O</c> / <c>Ctrl+S</c> 只有 <c>InputGestureText</c>（纯文字），
/// <c>Ctrl+R</c> 只写在 ToolTip 里——界面宣称可用，按下去毫无反应，
/// 而 WPF 的绑定失败是**静默**的（拼错的 <c>{Binding XxxCommand}</c> 不报错）。
/// 所以这里逐条把"宣称"和"实现"对上。
/// </para>
/// </summary>
public sealed class AppShellShortcutLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;

        // 显式失败信息：从 bin 目录往上找不到仓库根时，直接报路径，
        // 而不是让 dir!.FullName 抛一个没有上下文信息的 NRE。
        if (dir is null)
        {
            throw new FileNotFoundException(
                $"找不到 PeakCan.Host.slnx —— 测试是从非仓库目录运行的？起始目录: {AppContext.BaseDirectory}");
        }

        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }

    private static XDocument LoadXaml(params string[] relative)
        => XDocument.Load(RepoFile(relative));

    /// <summary>把 Modifiers 归一化（"Control+Shift" 与 "Shift+Control" 等价），空值用空串。</summary>
    private static string NormalizeModifiers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join(",",
            raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(m => m.ToLowerInvariant() switch
               {
                   "ctrl" or "control" => "Control",
                   "shift" => "Shift",
                   "alt" => "Alt",
                   "win" or "windows" => "Windows",
                   var other => other,
               })
               .OrderBy(m => m, StringComparer.Ordinal));
    }

    /// <summary>把 InputGestureText（如 "Ctrl+Shift+1"）翻译成 (Key, Modifiers) 口径。</summary>
    private static (string Key, string Modifiers) ParseGestureText(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var key = parts[^1];
        key = key.Length == 1 && char.IsDigit(key[0]) ? $"D{key}" : key.ToUpperInvariant();
        return (key, NormalizeModifiers(string.Join("+", parts[..^1])));
    }

    private static List<(string Key, string Modifiers, string? Command)> KeyBindings(XDocument doc)
        => doc.Descendants(P + "KeyBinding")
            .Select(kb => (
                (string?)kb.Attribute("Key") ?? "",
                NormalizeModifiers((string?)kb.Attribute("Modifiers")),
                (string?)kb.Attribute("Command")))
            .ToList();

    /// <summary>抽出 <c>{Binding XxxCommand}</c> 里的属性名；写法不符返回 null。</summary>
    private static string? CommandName(string? raw)
    {
        if (raw is null) return null;
        var match = Regex.Match(raw, @"^\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}$");
        return match.Success ? match.Groups[1].Value : null;
    }

    [Fact]
    public void Key_Map_Matches_The_Documented_Table()
    {
        var bindings = KeyBindings(LoadXaml("src", "PeakCan.Host.App", "AppShell.xaml"));

        // 键位表就是契约本身：改键位必须同时改这张表，改动会被显式看见而不是悄悄漂移。
        // **必须带命令名**：只比手势的话，把 Ctrl+1 从 ShowTrace 改成 ShowDbc
        // （菜单写着"追踪 Ctrl+1"、按下去切到 DBC）能全绿通过。
        var expected = new[]
        {
            // 主区域导航
            ("D1", "Control", "ShowTraceCommand"),
            ("D2", "Control", "ShowDbcCommand"),
            ("D3", "Control", "ShowReplayCommand"),
            ("D4", "Control", "ShowScriptCommand"),
            ("D5", "Control", "ShowXcpCommand"),
            // 右侧常驻面板导航
            ("D1", "Control,Shift", "ShowSendCommand"),
            ("D2", "Control,Shift", "ShowSignalsCommand"),
            ("D3", "Control,Shift", "ShowStatsCommand"),
            // 追踪页动作
            ("L", "Control", "ClearTraceFramesCommand"),
            ("P", "Control", "ToggleTracePauseCommand"),
            ("E", "Control", "ExportTraceCsvCommand"),
            // 全局动作
            ("R", "Control", "ToggleRecordingPanelCommand"),
            ("O", "Control", "OpenSessionCommand"),
            // Ctrl+N 新建脚本（只在脚本页生效，其它页写状态栏提示）
            ("N", "Control", "NewScriptCommand"),
            // Ctrl+S 按当前页分流（脚本页存脚本 / 其它页存会话），Ctrl+Shift+S 另存为脚本。
            ("S", "Control", "SaveCurrentCommand"),
            ("S", "Control,Shift", "SaveScriptAsCommand"),
            // 回放页动作：走帧刻意用 Ctrl+方向键（裸方向键会吞掉全应用的焦点导航）
            ("Left", "Control", "ReplayPrevFrameCommand"),
            ("Right", "Control", "ReplayNextFrameCommand"),
            ("B", "Control", "ReplayAddBookmarkCommand"),
        };

        bindings.Select(b => (b.Key, b.Modifiers, CommandName(b.Command)))
            .Should().BeEquivalentTo(expected, o => o.WithoutStrictOrdering());

        // 裸键（无修饰）一律不许出现在窗口级绑定里：KeyBinding 命中手势即吞键，
        // 与 CanExecute 无关，挂一个"经常不可用"的裸键等于废掉该键的全局默认行为。
        bindings.Should().OnlyContain(b => b.Modifiers.Length > 0,
            "窗口级快捷键必须带修饰键");
    }

    [Fact]
    public void No_Two_Bindings_Share_A_Gesture()
    {
        var gestures = KeyBindings(LoadXaml("src", "PeakCan.Host.App", "AppShell.xaml"))
            .Select(b => $"{b.Modifiers}|{b.Key}")
            .ToList();

        gestures.Should().OnlyHaveUniqueItems(
            "同一手势注册两次时，后者永远不会触发，且不会报错");
    }

    [Fact]
    public void Every_Bound_Command_Exists_On_AppShellViewModel()
    {
        var bindings = KeyBindings(LoadXaml("src", "PeakCan.Host.App", "AppShell.xaml"));
        bindings.Should().NotBeEmpty("AppShell 必须声明窗口级 InputBindings");

        foreach (var (key, modifiers, command) in bindings)
        {
            var propertyName = CommandName(command);
            propertyName.Should().NotBeNull(
                $"快捷键 {modifiers}+{key} 的命令写法应是 '{{Binding XxxCommand}}'，实际: {command}");

            var property = typeof(AppShellViewModel).GetProperty(propertyName!, BindingFlags.Public | BindingFlags.Instance);
            // WPF 里绑定到不存在的属性 = 静默失效，正是"假快捷键"的成因。
            property.Should().NotBeNull($"快捷键 {modifiers}+{key} 绑定的 {propertyName} 在 AppShellViewModel 上不存在");
            typeof(ICommand).IsAssignableFrom(property!.PropertyType)
                .Should().BeTrue($"{propertyName} 必须是 ICommand");
        }
    }

    [Fact]
    public void Advertised_Menu_Gestures_Are_Actually_Bound()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "AppShell.xaml");
        var bound = KeyBindings(doc).Select(b => (b.Key, b.Modifiers)).ToHashSet();

        var advertised = doc.Descendants(P + "MenuItem")
            .Select(mi => (string?)mi.Attribute("InputGestureText"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        advertised.Should().NotBeEmpty("视图/文件菜单应显示快捷键提示");
        foreach (var text in advertised)
        {
            var gesture = ParseGestureText(text);
            bound.Should().Contain(gesture,
                $"菜单宣称 {text} 可用，就必须有一条对应的 KeyBinding（否则又是只显示不生效）");
        }
    }

    [Fact]
    public void Trace_Toolbar_Advertised_Gestures_Are_Actually_Bound()
    {
        var appShellBound = KeyBindings(LoadXaml("src", "PeakCan.Host.App", "AppShell.xaml"))
            .Select(b => (b.Key, b.Modifiers)).ToHashSet();
        var traceDoc = LoadXaml("src", "PeakCan.Host.App", "Views", "TraceView.xaml");

        var advertised = traceDoc.Descendants()
            .Select(e => (string?)e.Attribute("ToolTip"))
            .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Contains("Ctrl+", StringComparison.Ordinal))
            .SelectMany(t => Regex.Matches(t!, @"\(([^()]*Ctrl\+[^()]*)\)").Select(m => m.Groups[1].Value))
            .ToList();

        advertised.Should().NotBeEmpty("追踪页高频按钮的 ToolTip 应写明键位");
        foreach (var text in advertised)
        {
            appShellBound.Should().Contain(ParseGestureText(text),
                $"ToolTip 宣称 {text} 可用，就必须在 AppShell 里真的绑上");
        }
    }

    [Fact]
    public void Replay_View_Keeps_Bare_Arrows_But_Not_CtrlB()
    {
        // 分工：裸 ←/→ 留在视图内（只影响回放页，且焦点在表格/Slider 时由控件自己消费）；
        // Ctrl+←/→ 与 Ctrl+B 在窗口级（回放 tab 激活即生效）。
        // 同一个手势只允许有一处定义，否则改一处忘另一处 = "有时灵有时不灵"。
        var viewBindings = KeyBindings(LoadXaml("src", "PeakCan.Host.App", "Views", "ReplayView.xaml"));

        viewBindings.Select(b => (b.Key, b.Modifiers, CommandName(b.Command)))
            .Should().BeEquivalentTo(new[]
            {
                ("Left", "", "PrevFrameCommand"),
                ("Right", "", "NextFrameCommand"),
            });

        // FluentAssertions 的 predicate 走表达式树，元组字面量编不过，先归一成字符串。
        var windowGestures = KeyBindings(LoadXaml("src", "PeakCan.Host.App", "AppShell.xaml"))
            .Select(b => $"{b.Modifiers}|{b.Key}")
            .ToHashSet();
        viewBindings.Select(b => $"{b.Modifiers}|{b.Key}")
            .Should().NotContain(g => windowGestures.Contains(g),
                "视图级与窗口级不得定义同一个手势");
    }
}
