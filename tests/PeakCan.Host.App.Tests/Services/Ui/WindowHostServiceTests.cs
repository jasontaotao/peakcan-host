using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed class WindowHostServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"winhost-{Guid.NewGuid():N}");
    private readonly List<string> _files = new();

    public WindowHostServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in _files)
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string TempPath() => Track(Path.Combine(_tempDir, $"state-{Guid.NewGuid():N}.json"));
    private string Track(string p) { _files.Add(p); return p; }

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

    // ---------- P0-5: geometry persistence (ApplyStoredState / SaveState) ----------

    [Fact]
    public void ApplyStoredState_RestoresGeometry_WhenStoreHasEntry()
    {
        RunSta(() =>
        {
            var store = new WindowStateStore(NullLogger<WindowStateStore>.Instance, TempPath());
            store.Set(WindowKey.Uds, new WindowStateDto(50, 60, 1100, 700, "Normal"));
            var win = new Window();

            WindowHostService.ApplyStoredState(win, WindowKey.Uds, store);

            win.Width.Should().Be(1100);
            win.Height.Should().Be(700);
            win.WindowState.Should().Be(WindowState.Normal);
        });
    }

    [Fact]
    public void ApplyStoredState_NoEntry_LeavesWindowUntouched()
    {
        RunSta(() =>
        {
            var store = new WindowStateStore(NullLogger<WindowStateStore>.Instance, TempPath());
            var win = new Window { Width = 500, Height = 400 };

            WindowHostService.ApplyStoredState(win, WindowKey.Hil, store);

            win.Width.Should().Be(500);
            win.Height.Should().Be(400);
        });
    }

    [Fact]
    public void Close_SavesGeometry_WhenStoreWired()
    {
        RunSta(() =>
        {
            var store = new WindowStateStore(NullLogger<WindowStateStore>.Instance, TempPath());
            var host = new WindowHostService(store);
            var win = host.Show(WindowKey.Uds, () => new Window())!;
            win.Width = 900;
            win.Height = 640;
            win.Left = 20;
            win.Top = 30;

            win.Close();

            var state = store.Get(WindowKey.Uds);
            state.Should().NotBeNull("closing a cached window must persist its geometry");
            state!.Width.Should().Be(900);
            state.Height.Should().Be(640);
        });
    }

    [Fact]
    public void SaveState_ThenApplyStoredState_RoundTrips()
    {
        RunSta(() =>
        {
            var store = new WindowStateStore(NullLogger<WindowStateStore>.Instance, TempPath());
            var win1 = new Window
            {
                Width = 800, Height = 600, Left = 100, Top = 200,
                WindowState = WindowState.Maximized,
            };

            WindowHostService.SaveState(win1, WindowKey.Hil, store);

            var win2 = new Window();
            WindowHostService.ApplyStoredState(win2, WindowKey.Hil, store);

            win2.Width.Should().Be(800);
            win2.Height.Should().Be(600);
            win2.WindowState.Should().Be(WindowState.Maximized);
        });
    }
}
