using System.Windows;
using FluentAssertions;
using PeakCan.Host.App.Services.Ui;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Ui;

/// <summary>
/// P0-2: pins <see cref="WindowHostService"/> behaviors:
/// <list type="number">
/// <item>Show registers a WindowEntry in OpenWindows with a display name;</item>
/// <item>repeated Show for the same key returns the same instance (no duplicate entry);</item>
/// <item>window close clears the cache and removes the entry;</item>
/// <item>distinct keys register independently;</item>
/// <item>Activate with no cached window is a silent no-op;</item>
/// <item>SetActive flips the entry's IsActive (menu check-state driver).</item>
/// </list>
/// WPF Window construction + events require an STA thread; the per-test
/// helper runs the body on one. No Application is created (tests run
/// Application-less, so Owner/IsAlive branches fall through defensively).
/// </summary>
public sealed class WindowHostServiceTests
{
    private static void RunSta(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            body();
            return;
        }
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (thread.IsAlive)
            throw new TimeoutException("STA thread did not complete within 30 s");
        if (caught is not null) throw caught;
    }

    [Fact]
    public void Show_RegistersEntry_WithDisplayName()
    {
        RunSta(() =>
        {
            var host = new WindowHostService();

            host.Show(WindowKey.TraceViewer, () => new Window());

            host.OpenWindows.Should().HaveCount(1);
            host.OpenWindows[0].Key.Should().Be(WindowKey.TraceViewer);
            host.OpenWindows[0].DisplayName.Should().Be("追踪查看器");
        });
    }

    [Fact]
    public void Show_SameKeyTwice_ReturnsSameWindow_NoDuplicateEntry()
    {
        RunSta(() =>
        {
            var host = new WindowHostService();
            var first = host.Show(WindowKey.Uds, () => new Window())!;

            var second = host.Show(WindowKey.Uds, () => new Window())!;

            second.Should().BeSameAs(first);
            host.OpenWindows.Should().HaveCount(1);
        });
    }

    [Fact]
    public void Close_RemovesEntry_AndClearsCache()
    {
        RunSta(() =>
        {
            var host = new WindowHostService();
            var win = host.Show(WindowKey.Uds, () => new Window())!;
            host.OpenWindows.Should().HaveCount(1);

            win.Close();

            host.OpenWindows.Should().BeEmpty();
            // Closed-reset: a subsequent Show builds a fresh window.
            var win2 = host.Show(WindowKey.Uds, () => new Window())!;
            win2.Should().NotBeSameAs(win);
        });
    }

    [Fact]
    public void Show_TwoDistinctKeys_RegisterIndependently()
    {
        RunSta(() =>
        {
            var host = new WindowHostService();
            host.Show(WindowKey.Uds, () => new Window());
            host.Show(WindowKey.Hil, () => new Window());

            host.OpenWindows.Should().HaveCount(2);
            host.OpenWindows.Select(e => e.Key)
                .Should().BeEquivalentTo(new[] { WindowKey.Uds, WindowKey.Hil });
        });
    }

    [Fact]
    public void Activate_WithNoCachedWindow_DoesNotThrow()
    {
        RunSta(() =>
        {
            var host = new WindowHostService();
            host.Activate(WindowKey.MultiFrame); // no window ever shown
        });
    }

    [Fact]
    public void SetActive_UpdatesEntryIsActive()
    {
        RunSta(() =>
        {
            var host = new WindowHostService();
            host.Show(WindowKey.TraceViewer, () => new Window());
            var entry = host.OpenWindows[0];
            entry.IsActive.Should().BeFalse();

            host.SetActive(WindowKey.TraceViewer, true);

            entry.IsActive.Should().BeTrue();
            host.SetActive(WindowKey.TraceViewer, false);
            entry.IsActive.Should().BeFalse();
        });
    }
}
