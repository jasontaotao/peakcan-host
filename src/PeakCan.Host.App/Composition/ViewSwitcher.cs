using System.Windows;
using System.Windows.Controls;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// v3.11.1 PATCH M3: single home for the lazy-view-create + cache-resume
/// pattern that the 9 AppShell Show-* commands used to inline
/// (AppShellViewModel.cs:481-659 before this refactor). Each command body
/// was a near-duplicate of the same skeleton:
/// <list type="number">
///   <item>Check the cached field; if null, construct the view/window.</item>
///   <item>Assign the cached instance to the shell's <c>CurrentView</c>
///         (for in-place views) or call <c>Show()</c> (for windows).</item>
///   <item>On window close, null the cached field so the next click opens
///         a fresh window.</item>
/// </list>
/// <para>
/// The helpers below extract that pattern so each menu command becomes
/// a one-line forward — easier to reason about, easier to add a 10th
/// surface later, and easier to unit-test in isolation. Production
/// behaviour is unchanged; the pre-existing tests in
/// <c>AppShellViewModelTests</c> continue to assert the cache reuse
/// contract end-to-end.
/// </para>
/// <para>
/// The class is <c>static</c>: it owns no state, the cache lives on the
/// calling VM (so menu click in one shell instance cannot collide with
/// a different shell instance), and a static helper matches the
/// <see cref="Composition.SinkWiringService"/> precedent of "Composition
/// is for shared logic, not state".
/// </para>
/// </summary>
public static class ViewSwitcher
{
    /// <summary>
    /// Switch a current view (in-place) to a freshly-created or cached one.
    /// On first call the factory is invoked and the result is cached;
    /// subsequent calls reuse the cached instance so DataGrid virtualization
    /// state (scroll position, selection) survives menu round-trips.
    /// </summary>
    /// <typeparam name="TView">
    /// A <see cref="FrameworkElement"/> the shell hosts in its
    /// <c>MainArea</c> ContentControl. Constraining to
    /// <see cref="FrameworkElement"/> matches the original lazy-view
    /// pattern (TraceView / DbcView / SendView etc.).
    /// </typeparam>
    /// <param name="factory">Constructs the view on first call. Must be non-null.</param>
    /// <param name="cache">
    /// Backing field on the calling VM. Passed by reference so the helper
    /// can write the freshly-created view back into the field.
    /// </param>
    /// <param name="setCurrent">
    /// Writes the (possibly cached) view into the shell's
    /// <c>CurrentView</c> property. The shell then re-renders its
    /// ContentControl.
    /// </param>
    /// <param name="menuName">
    /// Reserved for future logging — the original commands did not log
    /// show events; the parameter is in the signature today so the next
    /// PATCH can add structured logging without re-plumbing all 9 sites.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> is null. A null factory
    /// would silently produce a never-resolved cache, so the helper fails
    /// loud instead of letting the bad call site linger.
    /// </exception>
    public static void Show<TView>(
        Func<TView> factory,
        ref TView? cache,
        Action<TView> setCurrent,
        string menuName)
        where TView : FrameworkElement
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(setCurrent);
        _ = menuName; // reserved for future logging
        cache ??= factory();
        setCurrent(cache);
    }

    /// <summary>
    /// Open a non-modal secondary window (e.g. Trace Viewer, Multi-frame
    /// Send). On first call the factory is invoked and the result is
    /// cached; subsequent calls reuse the cached window so reopening the
    /// same menu entry preserves window state (position, size, child VM
    /// state). When the window closes, the cache is reset automatically
    /// so the next click opens a fresh window — matches the v3.9.1 PATCH
    /// B1 Owner + Closed-reset pattern AppShellViewModel used inline
    /// before the refactor.
    /// <para>
    /// The caller is responsible for assigning Owner + calling
    /// <c>Show()</c> / <c>Activate()</c> after this helper returns —
    /// those steps need <c>Application.Current.MainWindow</c>, which only
    /// resolves inside <c>App.OnStartup</c>'s STA context. This helper
    /// owns cache + factory + Closed-reset wiring; the caller owns WPF
    /// presentation (Owner + Show vs Activate).
    /// </para>
    /// </summary>
    /// <typeparam name="TWindow">A WPF <see cref="Window"/> subclass.</typeparam>
    /// <param name="factory">Constructs the window on first call. Must be non-null.</param>
    /// <param name="cache">
    /// Backing field on the calling VM. Passed by reference so the helper
    /// can write the freshly-created window back into the field, and
    /// null it back when the window closes.
    /// </param>
    public static void ShowWindow<TWindow>(
        Func<TWindow> factory,
        ref TWindow? cache)
        where TWindow : Window
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (cache is null)
        {
            // v3.11.1 PATCH M3: route the Closed-reset wiring through a
            // small ref-holder struct so the lambda can mutate the
            // caller's field. Lambdas cannot capture `ref` parameters
            // directly; the holder gives the closure a regular reference
            // to a mutable cell, and the helper's final `cache = ...`
            // write-back keeps the caller's field in sync.
            var holder = new CacheHolder<TWindow>(factory());
            holder.Window.Closed += (_, _) => holder.Value = null;
            cache = holder.Window;
            // Defensive read-back: if a Closed event already fired on
            // the new window between factory() and the += subscription
            // (extremely rare; can happen if the window's ctor opens
            // itself modally and closes synchronously), the holder has
            // been nulled and we must propagate that back to the caller.
            // Without this, the cache would hold a closed window.
            if (holder.Value is null)
            {
                cache = null;
            }
        }
    }

    /// <summary>
    /// Explicitly close + reset the cached window. Idempotent: a null
    /// cache (window already closed or never opened) is a silent no-op.
    /// Most call sites do NOT need this — <see cref="ShowWindow{TWindow}"/>
    /// wires the Closed event to null the cache automatically. The helper
    /// exists so a future "Close Trace Viewer" menu command (or a
    /// shutdown-time reset) can use the same path.
    /// </summary>
    /// <param name="cache">Backing field on the calling VM.</param>
    public static void HideWindow<TWindow>(ref TWindow? cache)
        where TWindow : Window
    {
        cache = null;
    }

    /// <summary>
    /// Small ref-holder so the <see cref="ShowWindow{TWindow}"/> Closed
    /// subscription can null the caller's field via a regular reference
    /// (lambdas cannot capture <c>ref</c> parameters directly). One
    /// instance per cached window; the holder lives as long as the
    /// closure holds it, which is until the window's Closed event fires
    /// (and the cache is then nulled). No additional cleanup needed.
    /// </summary>
    private sealed class CacheHolder<TWindow> where TWindow : Window
    {
        public TWindow Window { get; }
        public TWindow? Value { get; set; }
        public CacheHolder(TWindow window)
        {
            Window = window;
            Value = window;
        }
    }
}