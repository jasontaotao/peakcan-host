using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.ViewModels;
using Serilog;

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

    protected override void OnStartup(StartupEventArgs e)
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
        _host = AppHostBuilder.Build();
        // Start hosted services (SinkWiringService, DbcDecodeBackgroundService)
        // synchronously so their StartAsync runs before we resolve the shell.
        // Without this, SinkWiringService.StartAsync never fires and the
        // router only has the CanApi self-attach; Trace/Stats/Recording stay
        // empty even though the read loop is delivering frames. Discovered
        // 2026-06-26 via DIAG logging (sinks=1, dispatches=18000). See
        // OnExit below for the matching StopAsync on shutdown.
        //
        // Sync-over-async is safe here: Microsoft.Extensions.Hosting's
        // BackgroundService.StartAsync awaits ExecuteAsync on the
        // threadpool without capturing the WPF Dispatcher
        // SynchronizationContext, so the STA UI thread is never posted
        // back to during StartAsync. No deadlock risk.
        try
        {
            _host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "IHost.StartAsync threw during OnStartup");
            throw;
        }
        Services = _host.Services;
        var shell = new AppShell
        {
            DataContext = Services.GetRequiredService<AppShellViewModel>(),
        };
        shell.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Graceful shutdown: stop the IHost first so hosted services
        // (SinkWiringService, DbcDecodeBackgroundService) dispose
        // cleanly, then dispose the host to release the service
        // provider. Use GetAwaiter().GetResult() to block the shutdown
        // synchronously — OnExit is the only safe call site for
        // sync-over-async during process teardown. Cap the wait at
        // 10s so a mid-decode 64-byte FD frame or a long ChannelRouter
        // sink teardown has time to finish; the OS reaps the process
        // if shutdown still hangs.
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "IHost.StopAsync threw during OnExit");
        }
        finally
        {
            _host?.Dispose();
            _host = null;
            Services = null!;
        }
        base.OnExit(e);
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
