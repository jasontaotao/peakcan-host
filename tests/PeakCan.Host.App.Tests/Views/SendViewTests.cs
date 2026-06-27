using System.IO;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// v1.2.11 PATCH Item 3 + Item 5: <c>SendView.xaml</c> must contain
/// three sections — single-shot, cyclic (Expander), library (Expander).
/// These text-level tests guard the markup so a future XAML refactor
/// doesn't silently drop the cyclic / library UIs.
/// <para>
/// We use textual inspection of the compiled XAML file rather than
/// WPF runtime visual-tree traversal because the latter requires STA +
/// Application.Current — and xunit's parallel test classes share a
/// single AppDomain, so creating a second <see cref="System.Windows.Application"/>
/// after a sibling STA test left a leaked singleton throws
/// <c>InvalidOperationException</c>. The text approach is robust and
/// catches the same regression (missing expander element).
/// </para>
/// </summary>
public class SendViewTests
{
    private static readonly string XamlPath = Path.Combine(
        AppContext.BaseDirectory,
        // Project layout: tests/.../bin/Debug/net10.0-windows/ → walk up to find src XAML
        "..", "..", "..", "..", "..",
        "src", "PeakCan.Host.App", "Views", "SendView.xaml");

    private static string ReadXaml() => File.ReadAllText(Path.GetFullPath(XamlPath));

    [Fact]
    public void SendView_Xaml_Has_Cyclic_Expander()
    {
        var xaml = ReadXaml();
        Assert.Contains("x:Name=\"CyclicExpander\"", xaml);
        Assert.Contains("Header=\"Cyclic send\"", xaml);
    }

    [Fact]
    public void SendView_Xaml_Has_Library_Expander()
    {
        var xaml = ReadXaml();
        Assert.Contains("x:Name=\"LibraryExpander\"", xaml);
        Assert.Contains("Header=\"Frame library\"", xaml);
    }

    [Fact]
    public void SendView_Xaml_Has_All_Three_Sections()
    {
        // Guards against silent removal of any of: single-shot GroupBox,
        // cyclic Expander, library Expander.
        var xaml = ReadXaml();
        Assert.Contains("Header=\"Send frame\"", xaml);
        Assert.Contains("x:Name=\"CyclicExpander\"", xaml);
        Assert.Contains("x:Name=\"LibraryExpander\"", xaml);
    }
}