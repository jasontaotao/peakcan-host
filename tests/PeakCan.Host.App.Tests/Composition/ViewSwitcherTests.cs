using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Tests.Collections;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// v3.11.1 PATCH M3: pins the contract that <see cref="ViewSwitcher"/> is
/// the single home for the lazy-view-create / cache-resume pattern that
/// the 9 AppShell Show-* commands used to inline (AppShellViewModel.cs:481-659
/// before the refactor). <see cref="FrameworkElement"/> ctor throws on
/// MTA, so each test body runs on a fresh STA thread via
/// <see cref="RunSta"/> (same pattern as <c>AppShellViewModelTests</c>).
/// The <see cref="WpfAppTestCollection"/> membership prevents parallel
/// execution with the other STA-bound tests so they do not race on the
/// WPF Application singleton.
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class ViewSwitcherTests
{
    private sealed class FakeView : UserControl
    {
        public FakeView() { }
    }

    private sealed class FakeWindow : Window
    {
        public FakeWindow() { }
    }

    /// <summary>
    /// Run <paramref name="body"/> on an STA thread because the helper
    /// exercises WPF <see cref="FrameworkElement"/> ctors. xunit's MTA
    /// threadpool throws on every <see cref="FrameworkElement"/> ctor.
    /// Same pattern as <c>AppShellViewModelTests.RunSta</c>. Join uses a
    /// 30 s timeout so a stuck dispatcher never freezes the test runner.
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
    public void Show_FirstCall_CreatesViewFromFactory()
    {
        RunSta(() =>
        {
            var cache = (FakeView?)null;
            var observed = (FakeView?)null;
            var created = 0;

            ViewSwitcher.Show(
                factory: () => { created++; return new FakeView(); },
                cache: ref cache,
                setCurrent: v => observed = v,
                menuName: "Trace");

            created.Should().Be(1, "first Show must invoke the factory exactly once");
            cache.Should().NotBeNull("the freshly-created view must be cached for reuse");
            observed.Should().BeSameAs(cache,
                "setCurrent must receive the cached view, so the ContentControl binding shows it");
        });
    }

    [Fact]
    public void Show_SecondCall_ReturnsCachedView()
    {
        RunSta(() =>
        {
            var first = new FakeView();
            var cache = first;
            var observed = (FakeView?)null;
            var created = 0;

            ViewSwitcher.Show(
                factory: () => { created++; return new FakeView(); },
                cache: ref cache,
                setCurrent: v => observed = v,
                menuName: "Trace");

            created.Should().Be(0, "second Show must NOT call the factory — the cached view must be reused so DataGrid virtualization state survives menu round-trips");
            cache.Should().BeSameAs(first, "the cache reference must be the same instance as the first call");
            observed.Should().BeSameAs(first, "setCurrent must receive the cached view, not a fresh one");
        });
    }

    [Fact]
    public void Show_NullFactory_ThrowsArgumentNullException()
    {
        // ARRANGE — no STA needed: no FrameworkElement ctor runs on this
        // path. ArgumentNullException fires from the helper's first line.
        var cache = (FakeView?)null;

        var act = () => ViewSwitcher.Show(
            factory: null!,
            cache: ref cache,
            setCurrent: _ => { },
            menuName: "Trace");

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("factory");
    }

    [Fact]
    public void ShowWindow_FirstCall_CreatesAndOwns()
    {
        RunSta(() =>
        {
            var cache = (FakeWindow?)null;
            var created = 0;

            ViewSwitcher.ShowWindow(
                factory: () => { created++; return new FakeWindow(); },
                cache: ref cache);

            created.Should().Be(1, "first ShowWindow must invoke the factory exactly once");
            cache.Should().NotBeNull("the freshly-created window must be cached for reuse");
        });
    }

    [Fact]
    public void ShowWindow_CloseReset_ClearsCache()
    {
        RunSta(() =>
        {
            var cache = (FakeWindow?)null;
            ViewSwitcher.ShowWindow(
                factory: () => new FakeWindow(),
                cache: ref cache);
            cache.Should().NotBeNull("precondition: ShowWindow must populate the cache");

            ViewSwitcher.HideWindow(ref cache);

            cache.Should().BeNull("HideWindow must clear the cache so the next ShowWindow opens a fresh window");
        });
    }

    [Fact]
    public void HideWindow_OnNullCache_IsIdempotent()
    {
        // ARRANGE — no STA needed: HideWindow just nulls the cache;
        // no FrameworkElement ctor runs.
        var cache = (FakeWindow?)null;

        var act = () => ViewSwitcher.HideWindow(ref cache);

        act.Should().NotThrow("HideWindow must be idempotent — closing a never-opened window is a no-op");
        cache.Should().BeNull("cache must remain null when nothing was open");
    }
}