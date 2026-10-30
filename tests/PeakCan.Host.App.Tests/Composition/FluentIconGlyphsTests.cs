using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using FluentAssertions;
using PeakCan.Host.App.Composition.Icons;
using PeakCan.Host.App.Tests.Collections;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>P2-2: 每个码点常量必须真实存在于已安装的 Segoe Fluent Icons
/// 字体（C:\Windows\Fonts\SegoeIcons.ttf）。防机型差异——码点不存在时
/// 测试失败而非运行时静默渲染成豆腐块。</summary>
[Collection(WpfAppTestCollection.Name)]
public class FluentIconGlyphsTests
{
    private static HashSet<int> LoadFontGlyphs()
    {
        var font = new GlyphTypeface(new System.Uri(
            "file:///C:/Windows/Fonts/SegoeIcons.ttf"));
        return new HashSet<int>(font.CharacterToGlyphMap.Keys);
    }

    /// <summary>
    /// 反射枚举全部 <c>public const string</c> 码点。**不能用清单**：手写清单漏登记过
    /// 新加的图标，而"漏登记"的后果恰好就是本测试要防的那种——码点不存在时依旧全绿。
    /// </summary>
    public static IEnumerable<object[]> AllGlyphs()
        => typeof(FluentIconGlyphs)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => new object[] { (string)f.GetRawConstantValue()! })
            .ToList();

    [Theory]
    [MemberData(nameof(AllGlyphs))]
    public void Codepoint_Exists_In_Segoe_Fluent_Icons(string glyph)
    {
        glyph.Should().HaveLength(1, "每个常量是单字符码点");
        LoadFontGlyphs().Should().Contain(glyph[0],
            $"U+{(int)glyph[0]:X4} 必须存在于 SegoeIcons.ttf");
    }
}
