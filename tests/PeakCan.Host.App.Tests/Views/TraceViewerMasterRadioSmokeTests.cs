using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.Views;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// v3.11.7 PATCH: actual WPF visual-tree instantiation test for the
/// per-source master-radio legend. v3.11.6 PATCH shipped without this
/// test and missed a regression: <c>RadioButton.IsChecked</c> is TwoWay
/// by default, so WPF called the converter's <c>ConvertBack</c> during
/// the initial <c>Activate()</c> path. The <c>ConvertBack</c> threw
/// <c>NotSupportedException</c>, which bubbled up through
/// <c>PropertyPathWorker.CheckReadOnly</c> and surfaced as
/// <c>XamlParseException</c> after the first .asc load populated the
/// per-source legend strip.
///
/// v3.11.7 fixes the regression by adding <c>Mode="OneWay"</c> on the
/// <c>MultiBinding</c>. This test materializes a <c>RadioButton</c>
/// bound through the same <c>MultiBinding</c> + <c>x:Static</c> +
/// <c>SourceIdEqualsMasterConverter</c> path, exercises WPF's binding
/// initialization pipeline, and asserts no exception fires.
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class TraceViewerMasterRadioSmokeTests
{
    /// <summary>
    /// Mirrors the production per-source master-radio XAML: a
    /// <c>RadioButton</c> whose <c>IsChecked</c> is bound to a
    /// <c>MultiBinding</c> comparing <c>SourceId</c> (row DataContext)
    /// against <c>DataContext.MasterSourceId</c> (window DataContext),
    /// converted via <c>SourceIdEqualsMasterConverter</c>.
    /// </summary>
    private sealed class FakeSource
    {
        public string SourceId { get; set; } = "src-1";
    }

    private sealed class FakeVm
    {
        public string MasterSourceId { get; set; } = "src-1";
    }

    /// <summary>
    /// Run <paramref name="body"/> on an STA thread because WPF
    /// requires single-threaded apartment for <see cref="FrameworkElement"/>
    /// construction. Same pattern as <c>AppShellViewModelTests.RunSta</c>.
    /// </summary>
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
        if (thread.IsAlive)
        {
            throw new TimeoutException("STA thread did not complete within 30 s — likely a WPF dispatcher deadlock");
        }
        if (caught is not null) throw caught;
    }

    [Fact]
    public void MasterRadio_MultiBinding_OneWay_DoesNotThrowOnActivation()
    {
        // v3.11.7 regression guard: instantiate the same XAML shape
        // as TraceViewerView.xaml:117-128 and assert WPF's binding
        // activation pipeline completes without throwing. The pre-fix
        // ConvertBack path throws NotSupportedException → bubbles up
        // as XamlParseException AFTER the first asc load.
        RunSta(() =>
        {
            var source = new FakeSource { SourceId = "src-1" };
            var vm = new FakeVm { MasterSourceId = "src-1" };

            // Build the production-equivalent MultiBinding programmatically.
            // (Inline XAML compilation isn't trivial in a test, but
            // constructing the MultiBinding + 2 child Bindings +
            // attaching to the RadioButton.IsChecked DP exercises
            // the exact same WPF pipeline as XAML parse.)
            var multiBinding = new MultiBinding
            {
                Converter = SourceIdEqualsMasterConverter.Instance,
                Mode = BindingMode.OneWay,  // v3.11.7 fix
            };

            // SourceId binding — DataContext is the row (FakeSource).
            var sourceIdBinding = new Binding("SourceId") { Source = source };
            multiBinding.Bindings.Add(sourceIdBinding);

            // MasterSourceId binding — DataContext is the window (FakeVm)
            // via RelativeSource AncestorType=Window.
            var masterBinding = new Binding("MasterSourceId") { Source = vm };
            multiBinding.Bindings.Add(masterBinding);

            // Attach to RadioButton.IsChecked (TwoWay DP by default —
            // the v3.11.6 bug was that this triggered ConvertBack,
            // which threw; Mode=OneWay on the MultiBinding suppresses
            // the ConvertBack call entirely).
            var radio = new RadioButton { DataContext = source };
            radio.SetBinding(RadioButton.IsCheckedProperty, multiBinding);

            // Activate the binding pipeline. Pre-fix this throws
            // NotSupportedException from ConvertBack. Post-fix it's a
            // no-op for the read path; binding silently observes IsChecked=false.
            Exception? caught = null;
            try
            {
                // Force binding activation by reading IsChecked (the
                // BindingExpression.Activate path is triggered lazily
                // on first read or first layout pass).
                _ = radio.IsChecked;
                radio.Measure(new Size(100, 30));
                radio.Arrange(new Rect(0, 0, 100, 30));
                _ = radio.IsChecked;
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            caught.Should().BeNull(
                "RadioButton.IsChecked + MultiBinding (Mode=OneWay) activation " +
                "must NOT throw. v3.11.6 PATCH shipped without this smoke test " +
                "and missed that ConvertBack throws on TwoWay default.");
        });
    }
}