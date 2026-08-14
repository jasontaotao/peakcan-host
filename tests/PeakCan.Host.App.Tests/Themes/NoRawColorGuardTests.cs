using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Themes;

/// <summary>
/// §9 验收强制：16 个 XAML 无裸 hex/命名色（§5 保留的数据色除外）。
/// 扫描前剥离 XML 注释。hex 覆盖 3/6/8 位；命名色覆盖 WPF Colors 全集。
/// </summary>
public class NoRawColorGuardTests
{
    private static readonly string[] TokenizedFiles =
    {
        "AppShell.xaml",
        "Views/DbcTreePickerWindow.xaml", "Views/DbcView.xaml",
        "Views/HilView.xaml", "Views/RecordView.xaml", "Views/ReplayView.xaml",
        "Views/ScriptView.xaml", "Views/SendView.xaml", "Views/SignalView.xaml",
        "Views/TraceView.xaml", "Views/TraceViewerView.xaml",
        "Views/TraceViewerViewChatPanel.xaml",
        "Windows/ConnectionSettingsWindow.xaml",
        "Windows/EcuScriptEditorWindow.xaml",
        "Windows/MultiFrameSendWindow.xaml", "Windows/UdsWindow.xaml",
    };

    // 每文件允许保留的数据色（§5）：ScriptView 保留深色编辑器 #1E1E1E（D4）；
    // TraceViewerView 图表锚点命名色；ChatPanel 消息 chip 色。
    private static readonly (string file, string[] hex, string[] named)[] Allow =
    {
        ("Views/ScriptView.xaml", new[] { "#1E1E1E" }, Array.Empty<string>()),
        ("Views/TraceViewerView.xaml", Array.Empty<string>(),
            new[] { "Blue", "Green", "Red", "White" }),
        ("Views/TraceViewerViewChatPanel.xaml", new[] { "#DCF8C6" },
            new[] { "Green", "Red" }),
    };

    private static readonly Regex HexRe = new(@"#[0-9A-Fa-f]{3,8}\b");

    // 枚举 WPF Colors 静态类的全部命名色（约 140 个），替代硬编码列表：
    // 覆盖 Crimson / LightGreen / Maroon 等全部命名色，抗回归且随框架演进。
    private static readonly string[] AllNamedColors =
        typeof(System.Windows.Media.Colors).GetProperties()
            .Select(p => p.Name).ToArray();
    private static readonly Regex NamedRe = new(
        @"\b(" + string.Join("|", AllNamedColors.OrderByDescending(n => n.Length)) + @")\b",
        RegexOptions.IgnoreCase);

    private static string StripComments(string xaml) =>
        Regex.Replace(xaml, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void No_Raw_Hex_Or_Named_Colors_In_Tokenized_Views()
    {
        var root = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "PeakCan.Host.App");

        foreach (var file in TokenizedFiles)
        {
            var text = StripComments(
                File.ReadAllText(Path.GetFullPath(Path.Combine(root, file))));
            var allowHex = Allow.FirstOrDefault(a => a.file == file).hex ?? Array.Empty<string>();
            var allowNamed = Allow.FirstOrDefault(a => a.file == file).named ?? Array.Empty<string>();

            var badHex = HexRe.Matches(text)
                .Select(m => m.Value)
                .Where(h => !allowHex.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
            var badNamed = NamedRe.Matches(text)
                .Select(m => m.Value)
                .Where(n => !allowNamed.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Where(n => !n.Equals("Transparent", StringComparison.OrdinalIgnoreCase)).ToList();

            badHex.Should().BeEmpty($"{file} 不得有裸 hex（用 Colors.xaml 令牌）");
            badNamed.Should().BeEmpty($"{file} 不得有裸命名色（用 Colors.xaml 令牌）");
        }
    }
}
