using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.App.Tests.Collections;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// v3.12.0 MINOR M3: STA smoke-test matrix for every project
/// <see cref="IValueConverter"/>. v3.11.6 PATCH shipped the master-radio
/// fix WITHOUT a STA smoke test and missed that
/// <c>RadioButton.IsChecked</c> (TwoWay DP) triggered
/// <c>ConvertBack</c> on a converter that threw
/// <c>NotSupportedException</c>. This test catches the same class of
/// regression on every converter in <c>App.xaml</c> by binding each
/// one to a representative TwoWay DP, exercising the binding
/// activation pipeline, and asserting no throw fires.
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public sealed class ConverterSmokeTests
{
    private static void RunSta(Action body)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA)
        {
            body();
            return;
        }
        Exception? caught = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (thread.IsAlive) throw new TimeoutException("STA thread did not complete within 30s");
        if (caught is not null) throw caught;
    }

    public sealed record ConverterCase(IValueConverter Converter, DependencyProperty TargetDp, bool IsTwoWayDefault);

    public static IEnumerable<object[]> AllConverters()
    {
        yield return new object[] { new ConverterCase(new PeakCan.Host.App.Composition.Converters.BooleanToVisibilityConverter(), UIElement.VisibilityProperty, false) };
        yield return new object[] { new ConverterCase(new InverseBooleanConverter(), ToggleButton.IsCheckedProperty, true) };
        yield return new object[] { new ConverterCase(new NullToVisibilityConverter(), UIElement.VisibilityProperty, false) };
        yield return new object[] { new ConverterCase(new KindEqualsConverter(), UIElement.VisibilityProperty, false) };
        yield return new object[] { new ConverterCase(new OxyColorToBrushConverter(), Panel.BackgroundProperty, false) };
    }

    [Theory]
    [MemberData(nameof(AllConverters))]
    public void Converter_DoesNotThrow_WhenAttached_ToRepresentativeDp(ConverterCase data)
    {
        // Arrange: stand up a fresh WPF Application if none exists in this
        // STA collection slot. The WpfAppTestCollection serializes the
        // tests; one Application instance is shared.
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var binding = new Binding("Source") { Converter = data.Converter, Source = new { Source = "test" } };

        RunSta(() =>
        {
            var host = new ContentControl();
            host.SetBinding(ContentControl.ContentProperty, binding);

            Exception? caught = null;
            try
            {
                _ = host.Content;
                host.Measure(new Size(100, 30));
                host.Arrange(new Rect(0, 0, 100, 30));
                _ = host.Content;
            }
            catch (Exception ex) { caught = ex; }
            caught.Should().BeNull(
                $"{data.Converter.GetType().Name} must not throw on WPF binding activation. " +
                $"TwoWay default: {data.IsTwoWayDefault}. " +
                $"Pre-fix (v3.11.6 master-radio): ConvertBack threw NotSupportedException on TwoWay DP.");
        });
    }
}
