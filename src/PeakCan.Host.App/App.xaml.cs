using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using Serilog;
using SerilogLogger = Serilog.ILogger;

namespace PeakCan.Host.App;

/// <summary>
/// WPF application bootstrap. Builds the DI host in <see cref="OnStartup"/>,
/// stores the <see cref="IServiceProvider"/> on a static for ad-hoc resolution,
/// installs global exception handlers, and shows the <see cref="AppShell"/>
/// window with the <see cref="AppShellViewModel"/> resolved from DI.
/// <para>
/// We intentionally do <i>not</i> set <c>StartupUri</c> in <c>App.xaml</c>:
/// the shell's <c>DataContext</c> must come from the DI container so that
/// its constructor (ChannelRouter, ILogger, …) is invoked.
/// </para>
/// <para>
/// <b>Global exception handling:</b> every unhandled exception from the
/// .NET threadpool (<c>AppDomain.CurrentDomain.UnhandledException</c>),
/// the WPF dispatcher (<c>DispatcherUnhandledException</c>), and
/// unobserved task failures (<c>TaskScheduler.UnobservedTaskException</c>)
/// is logged at error / critical level through Serilog. The dispatcher
/// exception is intentionally <i>not</i> marked handled — a UI exception
/// has already corrupted the dispatcher loop, so a controlled crash with
/// a log line is safer than continuing in an undefined state.
/// </para>
/// <para>
/// <b>Host lifecycle:</b> the <see cref="IHost"/> is stored as
/// <see cref="_host"/> and disposed in <see cref="OnExit"/>. The previous
/// implementation leaked the host (local variable in OnStartup) so
/// hosted services like <c>SinkWiringService</c> never had
/// <c>StopAsync</c> called on application exit.
/// </para>
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Process-wide DI service provider, set during <see cref="OnStartup"/>.
    /// Exposed for view-model code that needs ad-hoc resolution (rare —
    /// prefer constructor injection). Never null after startup.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The composition root <see cref="IHost"/>. Stored so <see cref="OnExit"/>
    /// can call <c>StopAsync</c> + <c>Dispose</c> to gracefully shut down
    /// hosted services. Was previously a local variable in OnStartup,
    /// which meant the host (and every BackgroundService inside it) was
    /// never disposed on application exit.
    /// </summary>
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // v1.2.9: register the legacy code-page encoding provider
        // (GBK/CP936, CP932, CP949, etc.) before any DBC load
        // attempt. .NET Core / .NET 5+ only ships UTF-8/16/32 by
        // default; the DbcService.ReadDbcTextAsync helper falls
        // back to the system OEM code page on UTF-8 decode
        // failure, which throws NotSupportedException if the
        // provider isn't registered. Registration is idempotent
        // and process-global; safe to call here before DI
        // container construction.
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);
        // Install the global handlers BEFORE we touch the DI host, so
        // any failure during host construction is captured. The static
        // method is also exposed for test verification of the
        // subscription side-effect. The dispatcher handler is an
        // instance event on Application, so it is registered here
        // (not in the static helper).
        InstallStaticGlobalExceptionHandlers();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _host = new AppHostBuilder().Build();
        // v1.2.12: OnStartup is now async void so we can await
        // IHost.StartAsync without blocking the WPF STA thread. The
        // previous sync-over-async (GetAwaiter().GetResult()) was a
        // latent STA deadlock: any future hosted service that captures
        // the WPF Dispatcher SynchronizationContext in StartAsync
        // would have posted back to the UI thread we were blocking.
        // ConfigureAwait(false) ensures the continuation (including
        // _shell.Show()) runs on the threadpool, not back on STA.
        // BackgroundService.StartAsync itself awaits ExecuteAsync on
        // the threadpool without capturing the WPF SynchronizationContext,
        // so the STA UI thread is never posted back to during StartAsync.
        try
        {
            await _host.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failure path: the previous implementation rethrew, which
            // crashed the process inside OnStartup with no chance to log
            // through the WPF dispatcher. We now log via Serilog and
            // call Shutdown(1) so the application exits cleanly with a
            // non-zero exit code and the failure is visible in the log.
            Log.Logger.Fatal(ex, "IHost.StartAsync threw during OnStartup");
            Shutdown(1);
            return;
        }
        Services = _host.Services;
        // _shell.Show() runs on the threadpool after the await, so we
        // must Dispatcher.Invoke Show to guarantee the window is created
        // on the WPF STA thread. Without this, Show() would throw on
        // .NET 10: "The calling thread must be STA".
        var shell = new AppShell
        {
            DataContext = Services.GetRequiredService<AppShellViewModel>(),
        };
        // v3.6.0 MINOR T2: resolve the auto-saver + VM up front so the
        // post-Show dispatcher block can chain the restore prompt.
        var autoSaver = Services.GetRequiredService<TraceSessionAutoSaver>();
        var traceVm = Services.GetRequiredService<TraceViewerViewModel>();
        // v3.7.0 MINOR Chunk 3: chain a second restore prompt for the
        // Replay tab. Each prompt is independent — user can say Yes to
        // one and No to the other. Worst case: 2 MessageBoxes in a row
        // on app start (annoying but correct).
        var replaySaver = Services.GetRequiredService<ReplaySessionAutoSaver>();
        var replayVm = Services.GetRequiredService<ReplayViewModel>();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            // v3.9.1 PATCH Bug #1: explicitly assign Application.Current.MainWindow
            // BEFORE shell.Show() so non-modal secondary windows (Trace Viewer,
            // Multi-frame send, etc.) resolve a non-null owner for cascade-close.
            // WPF's default fallback is "first shown Window", which would
            // otherwise be Trace Viewer if it was opened earlier — fragile and
            // was the root cause of Trace Viewer surviving AppShell close.
            Application.Current.MainWindow = shell;
            shell.Show();
            try
            {
                await autoSaver.ApplyAutoSnapshotAsync(traceVm, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Auto-restore prompt failed");
            }
            try
            {
                await replaySaver.ApplyAutoSnapshotAsync(replayVm, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Replay auto-restore prompt failed");
            }
        });
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Graceful shutdown: stop the IHost first so hosted services
        // (SinkWiringService, DbcDecodeBackgroundService) dispose
        // cleanly, then dispose the host to release the service
        // provider. v1.2.12: OnExit is now async void (matching
        // OnStartup) so we can await StopAsync without blocking the
        // WPF STA thread. ConfigureAwait(false) keeps the continuation
        // off the dispatcher. Cap the wait at 10s so a mid-decode
        // 64-byte FD frame or a long ChannelRouter sink teardown has
        // time to finish; the OS reaps the process if shutdown still
        // hangs. OnExit is the only call site where we tolerate
        // exceptions during teardown — the process is exiting anyway.
        // v3.6.2 PATCH: the auto-save pre-flush + host-stop sequence
        // is extracted into <see cref="RunShutdownAsync"/> for unit
        // testability. The dispose in the finally remains here so the
        // caller still owns host lifetime.
        if (_host is not null)
        {
            try
            {
                await RunShutdownAsync(
                    _host,
                    sp => sp.GetService<TraceSessionAutoSaver>(),
                    sp => sp.GetService<ReplaySessionAutoSaver>(),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10),
                    Log.Logger).ConfigureAwait(false);
            }
            finally
            {
                _host.Dispose();
                _host = null;
                Services = null!;
            }
        }
        base.OnExit(e);
    }

    /// <summary>
    /// v3.6.2 PATCH: extracted shutdown sequence so the auto-save /
    /// host-stop ordering can be unit-tested without spinning up the
    /// full WPF host. <paramref name="autoSaverResolver"/> is
    /// injectable so tests can return a stub <see cref="TraceSessionAutoSaver"/>
    /// without a real <see cref="IServiceProvider"/>. <paramref name="logger"/>
    /// is explicit so the static <see cref="Log.Logger"/> lookup is
    /// never invoked from the test path. Both timeouts are explicit so
    /// the test can pass zero / near-zero durations.
    /// <para>
    /// <b>v3.7.0 MINOR Chunk 3:</b> signature extended with
    /// <paramref name="replayAutoSaverResolver"/> and
    /// <paramref name="replayAutoSaveTimeout"/>. The Trace auto-save
    /// runs first, then the Replay auto-save, then the host stops. The
    /// <see cref="AutoSavePrefs"/> file is shared (one opt-out flag for
    /// both tabs).
    /// </para>
    /// <para>
    /// <b>Ordering contract:</b> both auto-savers run to completion (or
    /// time out) BEFORE <see cref="IHost.StopAsync"/> is called. The
    /// auto-savers resolve their VMs through their own DI providers; if
    /// <c>StopAsync</c> ran first the service provider would be
    /// disposed and the resolvers would return null, silently
    /// skipping the saves.
    /// </para>
    /// <para>
    /// <b>Exception contract:</b> exceptions during auto-save are
    /// caught and logged at Warning (auto-save must never crash
    /// shutdown); exceptions during <c>StopAsync</c> are caught and
    /// logged at Error (host teardown failures are tolerated on the
    /// exit path). The method does NOT dispose the host — that is the
    /// caller's responsibility.
    /// </para>
    /// </summary>
    internal static async Task RunShutdownAsync(
        IHost host,
        Func<IServiceProvider, TraceSessionAutoSaver?> autoSaverResolver,
        Func<IServiceProvider, ReplaySessionAutoSaver?> replayAutoSaverResolver,
        TimeSpan autoSaveTimeout,
        TimeSpan replayAutoSaveTimeout,
        TimeSpan hostStopTimeout,
        SerilogLogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(autoSaverResolver);
        ArgumentNullException.ThrowIfNull(replayAutoSaverResolver);
        ArgumentNullException.ThrowIfNull(logger);

        // Pre-flush: best-effort auto-save BEFORE host teardown so we
        // never lose the user's current session. Cap so we don't blow
        // the shutdown budget if the disk is slow.
        // v3.7.0 Chunk 3: Trace first, then Replay.
        try
        {
            var autoSaver = autoSaverResolver(host.Services);
            if (autoSaver is not null)
            {
                using var cts = new CancellationTokenSource(autoSaveTimeout);
                await autoSaver.TrySaveAutoSnapshotAsync(cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Trace auto-save failed during OnExit");
        }

        try
        {
            var replaySaver = replayAutoSaverResolver(host.Services);
            if (replaySaver is not null)
            {
                using var cts = new CancellationTokenSource(replayAutoSaveTimeout);
                await replaySaver.TrySaveAutoSnapshotAsync(cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Replay auto-save failed during OnExit");
        }

        // Host stop: graceful teardown of hosted services. We call
        // the IHostedService StopAsync(CancellationToken) overload
        // directly (instead of the IHost.StopAsync(TimeSpan) DIM)
        // so unit tests can substitute IHost without worrying about
        // default-interface-method dispatch. The CancellationToken
        // cancellation is enforced by a linked CTS — the same
        // shape the DIM uses internally — so behavior is preserved.
        using var stopCts = new CancellationTokenSource(hostStopTimeout);
        try
        {
            await host.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "IHost.StopAsync threw during OnExit");
        }
    }

    /// <summary>
    /// Wire up the static global exception handlers (AppDomain +
    /// TaskScheduler). The dispatcher handler is registered in
    /// <see cref="OnStartup"/> because it is an instance event.
    /// Public for test seam — production callers should let
    /// <see cref="OnStartup"/> invoke it. Idempotent (uses a static
    /// guard) so a unit test can re-invoke without doubling the
    /// subscription.
    /// </summary>
    internal static void InstallStaticGlobalExceptionHandlers()
    {
        if (_handlersInstalled) return;
        _handlersInstalled = true;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }
    private static bool _handlersInstalled;

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Logger.Error(ex,
            "AppDomain.CurrentDomain.UnhandledException (terminating={Terminating})",
            e.IsTerminating);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Logger.Error(e.Exception,
            "DispatcherUnhandledException on thread {Thread}",
            System.Threading.Thread.CurrentThread.Name ?? "<unnamed>");
        // Do not mark Handled: the dispatcher loop is already in an
        // undefined state. Let WPF terminate the process — the log
        // gives us the diagnostic info the user previously had no
        // way to obtain.
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Logger.Error(e.Exception, "UnobservedTaskException");
        // Mark observed so the process does not crash; the log line
        // is the diagnostic surface.
        e.SetObserved();
    }
}
