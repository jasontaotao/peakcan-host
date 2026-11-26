using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

public class ByteLayoutViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        return XDocument.Load(Path.Combine(
            dir!.FullName, "src", "PeakCan.Host.App", "Views", "ByteLayoutView.xaml"));
    }

    [Fact]
    public void View_Contains_Uniform_Viewbox_And_Mux_ComboBox()
    {
        var doc = Load();

        var viewbox = doc.Descendants(P + "Viewbox").Single();
        ((string?)viewbox.Attribute("Stretch")).Should().Be("Uniform");

        var combo = doc.Descendants(P + "ComboBox").Single();
        ((string?)combo.Attribute("ItemsSource")).Should().Contain("MuxValues");
        ((string?)combo.Attribute("SelectedItem")).Should().Contain("SelectedMuxValue");
    }

    [Fact]
    public void Cells_Are_Keyboard_Focusable_And_Have_Accessible_Names()
    {
        var doc = Load();

        var border = doc.Descendants(P + "Border").Single(
            b => ((string?)b.Attribute("Focusable")) == "True");

        ((string?)border.Attribute("AutomationProperties.Name")).Should().Contain("Tooltip");
        border.Attribute("MouseEnter").Should().NotBeNull();
        border.Attribute("GotFocus").Should().NotBeNull();
    }

    [Fact]
    public void Zero_Bits_Are_Dimmed_Through_An_IsSet_Trigger()
    {
        var doc = Load();

        var border = doc.Descendants(P + "Border").Single(
            b => ((string?)b.Attribute("Focusable")) == "True");

        var trigger = border.Descendants(P + "DataTrigger")
            .SingleOrDefault(t => ((string?)t.Attribute("Binding"))?.Contains(
                "IsSet", StringComparison.Ordinal) == true);

        trigger.Should().NotBeNull("0 位必须通过 IsSet 触发变淡");
        ((string?)trigger!.Attribute("Value")).Should().Be("False");
        trigger.Descendants(P + "Setter")
            .Single(s => ((string?)s.Attribute("Property")) == "Opacity")
            .Attribute("Value")!.Value.Should().Be("0.28");
    }
}
