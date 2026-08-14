using System.Collections.ObjectModel;
using System.Windows;

namespace PeakCan.Host.App.Services.Ui;

/// <summary>
/// P0-2: single home for the 5 secondary-window lifecycles (Trace Viewer /
/// UDS / ECU Script Editor / Multi-frame / HIL) and the Window-menu
/// registry. Replaces the per-ViewModel cache fields (which previously
/// diverged — e.g. Multi-frame had two independent windows via the AppShell
/// vs SendView entries).
/// <para>
/// <c>Show(key, factory)</c> owns cache + factory + Closed-reset + the
/// OpenWindows registry + Owner assignment. It does <b>not</b> call
/// <c>Show()</c>/<c>Activate()</c> — the caller owns WPF presentation
/// (same split as the legacy <c>ViewSwitcher.ShowWindow</c> caller), so the
/// service stays testable without an Application. The window entry's
/// <see cref="WindowEntry.ActivateCommand"/> routes back through
/// <see cref="Activate(WindowKey)"/>, which re-shows/activates the cached
/// window (safe there because a menu click implies it was shown before).
/// </para>
/// </summary>
public sealed partial class WindowHostService
{
    private readonly Dictionary<WindowKey, Window> _cache = new();
    private readonly Dictionary<WindowKey, WindowEntry> _entries = new();
    private readonly ObservableCollection<WindowEntry> _openWindows = new();

    /// <summary>Live registry of open secondary windows, bound by the
    /// Window menu. Mutations happen on the UI thread.</summary>
    public ObservableCollection<WindowEntry> OpenWindows => _openWindows;

    /// <summary>
    /// Return the cached window for <paramref name="key"/> if it is still
    /// alive; otherwise build one via <paramref name="factory"/>, cache it,
    /// wire Closed/Activated/Deactivated, assign Owner (MainWindow), and
    /// register a Window menu entry. Caller then does Show/Activate.
    /// </summary>
    public Window? Show(WindowKey key, Func<Window> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (_cache.TryGetValue(key, out var cached) && IsAlive(cached))
        {
            return cached;
        }

        var win = factory();
        _cache[key] = win;

        // Register the menu entry (once per key).
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new WindowEntry(this, key, DisplayNameOf(key));
            _entries[key] = entry;
            _openWindows.Add(entry);
        }

        win.Closed += (_, _) =>
        {
            _cache.Remove(key);
            if (_entries.Remove(key, out var removed))
            {
                _openWindows.Remove(removed);
            }
        };
        win.Activated += (_, _) => SetActive(key, true);
        win.Deactivated += (_, _) => SetActive(key, false);

        // No IsAlive check on a freshly-built window — a brand-new window is
        // not yet in Application.Windows, so a Contains() check would reject
        // every first open. If a factory ever returns an already-closed
        // window, the stale cache entry self-heals on the next Show: the
        // cached-window branch above (IsAlive) misses and rebuilds.

        // Owner is centralized here so closing AppShell cascade-closes the
        // secondary window (v3.9.1 Bug #1 pattern). Defensive null/self
        // check: Application.Current is null in unit tests.
        if (Application.Current?.MainWindow is { } owner && owner != win)
        {
            win.Owner = owner;
        }

        return win;
    }

    /// <summary>Bring the cached window for <paramref name="key"/> to the
    /// front, re-showing it if it was hidden. Silent no-op if never opened.</summary>
    public void Activate(WindowKey key)
    {
        if (_cache.TryGetValue(key, out var win) && IsAlive(win))
        {
            if (!win.IsVisible)
            {
                win.Show();
            }
            win.Activate();
        }
    }

    /// <summary>Test seam + cross-flow access (e.g. SaveSessionAsync reads the
    /// Trace Viewer window's DataContext): the currently cached window for
    /// <paramref name="key"/>, or null if never opened / already closed.</summary>
    internal Window? GetCached(WindowKey key) =>
        _cache.TryGetValue(key, out var win) ? win : null;

    /// <summary>Test seam + Activated/Deactivated wiring target: flips the
    /// menu entry's check-state. Internal — visible to App.Tests.</summary>
    internal void SetActive(WindowKey key, bool value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.IsActive = value;
        }
    }

    /// <summary>WPF has no public IsClosed; "still alive" is membership in
    /// <c>Application.Current.Windows</c>. In Application-less test threads
    /// we trust the cache (Closed-reset still runs there).</summary>
    private static bool IsAlive(Window win)
    {
        var app = Application.Current;
        if (app is null)
        {
            return true;
        }
        return app.Windows.Cast<Window>().Any(w => ReferenceEquals(w, win));
    }

    private static string DisplayNameOf(WindowKey key) => key switch
    {
        WindowKey.TraceViewer => "追踪查看器",
        WindowKey.Uds => "UDS 诊断",
        WindowKey.MultiFrame => "批量发送",
        WindowKey.EcuScriptEditor => "ECU 脚本编辑器",
        WindowKey.Hil => "HIL 测试",
        _ => key.ToString(),
    };
}
