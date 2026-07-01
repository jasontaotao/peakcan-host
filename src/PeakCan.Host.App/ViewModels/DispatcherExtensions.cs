using System.Windows.Threading;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// WPF dispatcher chokepoint for cross-thread <c>ObservableCollection</c>
/// mutation. Three production VMs (<c>DbcViewModel</c>,
/// <c>SignalViewModel</c>, <c>StatsViewModel</c>) subscribe to events
/// raised on a worker / timer / SDK read thread, and the bound
/// <c>ItemsControl</c> throws
/// <c>NotSupportedException: This type of CollectionView does not support
/// changes to its SourceCollection from a thread different from the
/// Dispatcher thread</c> if the mutation happens off the UI dispatcher.
/// <para>
/// Three paths land in <see cref="RunOnUi"/>:
/// </para>
/// <list type="bullet">
///   <item><b>No Application</b> (xunit test context without
///     <see cref="System.Windows.Application.Current"/> set) — run inline.
///     This is the only path most App.Tests exercise.</item>
///   <item><b>Caller is on the UI dispatcher thread</b> (or the dispatcher's
///     <c>CheckAccess</c> returns true) — run inline. Common during
///     startup before any background pump has been wired.</item>
///   <item><b>Different thread, live dispatcher</b> (production worker
///     thread → UI thread) — <see cref="Dispatcher.Invoke"/> (sync) so the
///     caller observes the post-state before continuing.</item>
/// </list>
/// <para>
/// <b>Test-leak fallback:</b> a previous STA test may have left a
/// <see cref="System.Windows.Application"/> whose dispatcher thread has
/// since exited. In that scenario <c>Dispatcher.Invoke</c> would block
/// forever, so we additionally check <see cref="Dispatcher.Thread"/>'s
/// liveness and fall back to inline. The same pattern was attempted in
/// Task 19 (commit <c>213af1b</c>) but was inverted — the guard used
/// <c>appDispatcher == callingDispatcher</c> to <i>suppress</i> the hop,
/// which also suppressed it for the production case (worker vs UI are
/// always different dispatchers). See
/// <c>docs/.../v0.2.0-hotfix-dispatcher-marshal.md</c> for the
/// regression report.
/// </para>
/// <para>
/// <b>Why an extension method, not a base class or service:</b> each
/// consumer is a <c>sealed partial</c> VM that already inherits
/// <c>ObservableObject</c>. A static helper keeps the inheritance chain
/// flat and makes the call site read as a verb (<c>RunOnUi(...)</c>) which
/// matches the surrounding guard comment style.
/// </para>
/// </summary>
internal static class DispatcherExtensions
{
    /// <summary>
    /// Run <paramref name="action"/> on the WPF UI dispatcher (blocking).
    /// See the class doc-comment for the full path decision.
    /// <para>
    /// <b>Do not call from <c>async</c> methods that resume on a captured
    /// UI <c>SynchronizationContext</c></b> (classic deadlock). Use
    /// <see cref="RunOnUiPost(Action)"/> for fire-and-forget semantics or
    /// marshal before the first <c>await</c>.
    /// </para>
    /// </summary>
    public static void RunOnUi(this Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var appDispatcher = System.Windows.Application.Current?.Dispatcher;

        // Path 1 (test): no Application, or caller already on the UI
        // dispatcher. Run inline.
        if (appDispatcher is null || appDispatcher.CheckAccess())
        {
            action();
            return;
        }

        // Path 2 / 3 (test-leak or production): try to marshal. If the
        // dispatcher is dead (thread exited) OR in the middle of shutdown
        // (race between our IsAlive check and a concurrent Shutdown call,
        // or a previous STA test left a half-shut-down singleton) the
        // Invoke throws InvalidOperationException. Fall back to inline so
        // the test pool keeps making progress and so a transient shutdown
        // race doesn't propagate as a hard crash to the worker.
        //
        // The previous guard (`Thread.IsAlive` only) missed the
        // half-shut-down case; the try/catch catches it.
        try
        {
            if (appDispatcher.Thread.IsAlive)
            {
                appDispatcher.Invoke(action);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Dispatcher has been shut down (or is in the middle of
            // CriticalShutdown); fall through to inline.
        }
        action();
    }

    /// <summary>
    /// Fire-and-forget variant of <see cref="RunOnUi(Action)"/> for hot
    /// paths where blocking the producer thread is unacceptable
    /// (e.g. <c>SignalViewModel</c> at ~8 kfps, <c>StatsViewModel</c> at
    /// 1 Hz). Returns immediately; the work runs on the UI dispatcher
    /// when it next pumps.
    /// <para>
    /// <b>Caveats:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>Returns <c>void</c> (deliberately non-<c>Task</c>) to
    ///     avoid the <c>XxxAsync</c>-without-<c>Task</c> trap. The name
    ///     <c>RunOnUiPost</c> signals "post" (fire-and-forget) rather
    ///     than "await".</item>
    ///   <item>Exceptions in <paramref name="action"/> surface on the UI
    ///     thread via <c>Application.DispatcherUnhandledException</c> in
    ///     production. In test contexts with a shut-down dispatcher they
    ///     may be swallowed — the test-leak fall-through to inline is
    ///     the canonical signal that the test environment is mis-configured.</item>
    /// </list>
    /// </summary>
    public static void RunOnUiPost(this Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var appDispatcher = System.Windows.Application.Current?.Dispatcher;

        if (appDispatcher is null || appDispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            if (appDispatcher.Thread.IsAlive)
            {
                // Discard the DispatcherOperation: the caller is
                // fire-and-forget by design.
                _ = appDispatcher.InvokeAsync(action);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Shutting down — fall through to inline.
        }
        action();
    }
}
