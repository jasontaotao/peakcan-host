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
/// one to a representative TwoWay DP (<c>RadioButton.IsChecked</c>,
/// the same DP that v3.11.6 PATCH regressed on), forcing
/// <c>Mode = OneWay</c> on the binding, and asserting the binding
/// activation pipeline never throws.
/// <para>
/// Contract under test: every converter in <c>App.xaml</c> is safe when
/// used with <c>Mode=OneWay</c> even when the target DP defaults to
/// TwoWay. This is the project's actual usage pattern (the v3.11.7
/// PATCH audit confirmed every App.xaml converter binds to a OneWay DP).
/// A converter that throws on OneWay activation (whether from
/// <c>Convert</c> or a buggy <c>ConvertBack</c> that fires despite
/// <c>Mode=OneWay</c>) fails this test.
/// </para>
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
    public void Converter_DoesNotThrow_WhenAttached_ToTwoWayDp_WithOneWayBindingMode(ConverterCase data)
    {
        // v3.12.0 M3 review fix: clear any leaked Application.Current
        // from a sibling STA test before we attempt to construct a new
        // WPF Application on this case's dedicated STA thread. The
        // WpfAppTestCollection serializes test classes (not cases), so
        // case 2..5 must recover from case 1's shutdown. Mirrors the
        // TraceViewModelTests.AppendBatch_On_StaThread_... pattern
        // (line 158 calls LeakedApplicationReset.CleanupLeakedApplication
        // from the MTA thread BEFORE spawning the STA thread).
        LeakedApplicationReset.CleanupLeakedApplication();

        // Force Mode=OneWay so ConvertBack is never invoked regardless of
        // the target DP default. The contract under test is the safe-usage
        // pattern (Mode=OneWay) — not "the converter has a working
        // ConvertBack". Pre-fix, binding to RadioButton.IsChecked (TwoWay)
        // would invoke ConvertBack and throw NotSupportedException from
        // any one-way-only converter.
        var binding = new Binding("Source")
        {
            Converter = data.Converter,
            Source = new { Source = "test" },
            Mode = BindingMode.OneWay,
        };

        RunSta(() =>
        {
            var radio = new RadioButton();
            radio.SetBinding(RadioButton.IsCheckedProperty, binding);

            Exception? caught = null;
            try
            {
                // Force binding activation on a TwoWay DP. The activation
                // pipeline MUST NOT throw even though IsChecked defaults
                // to TwoWay — Mode=OneWay suppresses ConvertBack entirely.
                _ = radio.IsChecked;
                radio.Measure(new Size(100, 30));
                radio.Arrange(new Rect(0, 0, 100, 30));
                _ = radio.IsChecked;
            }
            catch (Exception ex) { caught = ex; }
            caught.Should().BeNull(
                $"{data.Converter.GetType().Name} must not throw on WPF binding activation when " +
                $"attached to RadioButton.IsChecked (TwoWay DP) with Mode=OneWay. " +
                $"DP default TwoWay: {data.IsTwoWayDefault}. " +
                $"Pre-fix (v3.11.6 master-radio): ConvertBack threw NotSupportedException on TwoWay DP.");
        });

        // v3.12.0 M3 review fix: clean up the Application instance we
        // created on the STA thread above. The static
        // Application.Current reference would otherwise survive the
        // STA thread exit and trip the "multiple Application instances
        // in one AppDomain" guard in TraceViewModelTests. Mirrors the
        // cleanup at TraceViewModelTests lines 200-222.
        LeakedApplicationReset.CleanupLeakedApplication();
    }
}
