using System.IO;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;
using Xunit;

namespace PeakCan.Host.App.Tests.Themes;

/// <summary>P2-1: 令牌全集存在性 + 色值。用 XamlReader 解析 Colors.xaml
/// （无 Application 依赖），断言每个语义令牌都是期望的 SolidColorBrush。
/// 防止实现时漏令牌或写错色值。</summary>
[Collection(WpfAppTestCollection.Name)]
public class ColorTokensTests
{
    private static readonly string Path =
        System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PeakCan.Host.App", "Themes", "Colors.xaml");

    private static ResourceDictionary Load()
    {
        var xaml = File.ReadAllText(System.IO.Path.GetFullPath(Path));
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    [Theory]
    [InlineData("CanvasBg", "#F3F4F6")]
    [InlineData("Surface", "#FFFFFF")]
    [InlineData("SurfaceSubtle", "#F7F8FA")]
    [InlineData("RowAlternate", "#F7F8FA")]
    [InlineData("RowHover", "#EDF2F9")]
    [InlineData("RowSelected", "#DCECFB")]
    [InlineData("Border", "#D4D9DF")]
    [InlineData("BorderSubtle", "#E4E8EC")]
    [InlineData("Divider", "#E9EDF1")]
    [InlineData("TextPrimary", "#1B1F24")]
    [InlineData("TextSecondary", "#5A6470")]
    [InlineData("TextDisabled", "#9AA3AE")]
    [InlineData("TextOnAccent", "#FFFFFF")]
    [InlineData("Accent", "#0B5CAD")]
    [InlineData("AccentHover", "#094B8F")]
    [InlineData("AccentPressed", "#073D75")]
    [InlineData("Ok", "#1A7F37")]
    [InlineData("OkBg", "#E6F4EA")]
    [InlineData("WarnText", "#8A5B00")]
    [InlineData("WarnBg", "#FFF6E0")]
    [InlineData("WarnBorder", "#E3B64B")]
    [InlineData("Error", "#D62728")]
    [InlineData("ErrorBg", "#FDEBEC")]
    [InlineData("Info", "#0B5CAD")]
    [InlineData("FrameBgFd", "#E3F2FD")]
    [InlineData("FrameBgError", "#FFCDD2")]
    [InlineData("FrameBgHighlight", "#FFFDE7")]
    [InlineData("ConsoleBg", "#FBFBFC")]
    [InlineData("ConsoleFg", "#24292F")]
    [InlineData("ConsoleAccent", "#0550AE")]
    public void Token_Exists_With_Expected_Color(string key, string hex)
    {
        var rd = Load();
        var brush = rd[key].Should().BeOfType<SolidColorBrush>().Subject;
        var expected = (Color)ColorConverter.ConvertFromString(hex);
        brush.Color.Should().Be(expected, $"{key} 色值必须与 §5 一致");
    }

    [Theory]
    [InlineData("FontMono", "Consolas")]
    [InlineData("FontUI", "Segoe UI")]
    public void FontToken_Exists(string key, string family)
    {
        var rd = Load();
        rd[key].Should().BeOfType<FontFamily>()
            .Which.Source.Should().Be(family);
    }
}
