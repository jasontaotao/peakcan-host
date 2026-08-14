using System.Collections.Generic;
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

    public static IEnumerable<object[]> AllGlyphs()
    {
        yield return new object[] { FluentIconGlyphs.Settings };
        yield return new object[] { FluentIconGlyphs.Play };
        yield return new object[] { FluentIconGlyphs.Pause };
        yield return new object[] { FluentIconGlyphs.Stop };
        yield return new object[] { FluentIconGlyphs.Save };
        yield return new object[] { FluentIconGlyphs.OpenFolder };
        yield return new object[] { FluentIconGlyphs.Dismiss };
        yield return new object[] { FluentIconGlyphs.Search };
        yield return new object[] { FluentIconGlyphs.ChevronUp };
        yield return new object[] { FluentIconGlyphs.ChevronDown };
        yield return new object[] { FluentIconGlyphs.ChevronLeft };
        yield return new object[] { FluentIconGlyphs.ChevronRight };
        yield return new object[] { FluentIconGlyphs.Link };
        yield return new object[] { FluentIconGlyphs.Lightning };
        yield return new object[] { FluentIconGlyphs.Record };
        yield return new object[] { FluentIconGlyphs.Plug };
        yield return new object[] { FluentIconGlyphs.Power };
        yield return new object[] { FluentIconGlyphs.Bot };
        yield return new object[] { FluentIconGlyphs.Sparkle };
        yield return new object[] { FluentIconGlyphs.Replay };
        yield return new object[] { FluentIconGlyphs.Laptop };
        yield return new object[] { FluentIconGlyphs.Help };
    }

    [Theory]
    [MemberData(nameof(AllGlyphs))]
    public void Codepoint_Exists_In_Segoe_Fluent_Icons(string glyph)
    {
        glyph.Should().HaveLength(1, "每个常量是单字符码点");
        LoadFontGlyphs().Should().Contain(glyph[0],
            $"U+{(int)glyph[0]:X4} 必须存在于 SegoeIcons.ttf");
    }
}
