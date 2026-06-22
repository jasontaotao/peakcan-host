using System.Globalization;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;
using Serilog;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Composes the WPF process: a file-rotating Serilog logger, the
/// <see cref="ChannelRouter"/> + <see cref="BusStatisticsCollector"/> from
/// Infrastructure, the App-layer services and view-models, and the
/// <see cref="AppShell"/> window.
/// <para>
/// <see cref="Build"/> is idempotent only with respect to DI: it may be
/// called once at startup, and the returned <see cref="IHost"/> owns the
/// Serilog lifetime (it is disposed when the host is disposed).
/// </para>
/// <para>
/// Side effects on <see cref="Log.Logger"/>: this method sets the global
/// static Serilog logger. Tests that need a clean Serilog state must
/// reset it themselves; the production app does not care.
/// </para>
/// </summary>
public static class AppHostBuilder
{
    /// <summary>
    /// PEAK PCAN-USB FD first-channel handle. Per the inline amendment to
    /// Task 12, MVP probes a single hardcoded handle and does not
    /// enumerate; v1.1 will add multi-channel enumeration.
    /// </summary>
    public const ushort PcanUsbFdFirstHandle = 0x51;

    public static IHost Build()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeakCan.Host", "logs", "peak-.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: true);

        // Core infrastructure
        builder.Services.AddSingleton<ChannelRouter>();
        builder.Services.AddSingleton<BusStatisticsCollector>();
        // Task 18: extracted PEAK SDK probe call into a swappable
        // service so the App assembly has no Peak.Can.Basic dependency
        // (enforced by LayeringRulesTests.App_Should_Not_Depend_On_Peak_Can_Basic).
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelProbe,
                                       PeakCan.Host.Infrastructure.Peak.PeakChannelProbe>();

        // v0.4.0: multi-channel enumerator. Probes PCAN-USB 1–16.
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelEnumerator,
                                       PeakCan.Host.Infrastructure.Peak.PeakChannelEnumerator>();

        // Task T3 (H4): the App-layer VM no longer news PeakCanChannel
        // directly; it asks the factory for an ICanChannel. Production DI
        // binds the PEAK implementation; tests inject a fake to drive the
        // connect/disconnect state machine without hardware.
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelFactory,
                                      PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>();

        // v0.4.0: IPcanReader abstracts the PEAK SDK read calls so
        // PeakCanChannel's read loop can be unit-tested without hardware.
        builder.Services.AddSingleton<PeakCan.Host.Infrastructure.Peak.IPcanReader,
                                      PeakCan.Host.Infrastructure.Peak.PcanReader>();

        // App services
        builder.Services.AddSingleton<TraceService>();
        builder.Services.AddSingleton<SendService>();
        builder.Services.AddSingleton<DbcService>();
        builder.Services.AddSingleton<StatisticsService>();
        // M11: DBC lookup + signal decode runs off the SDK read thread on
        // its own worker. Registered as both a singleton (so SinkWiringService
        // gets the same instance the host starts) and a hosted service
        // (so BackgroundService.StartAsync fires the worker loop).
        builder.Services.AddSingleton<DbcDecodeBackgroundService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DbcDecodeBackgroundService>());

        // ViewModels
        builder.Services.AddSingleton<AppShellViewModel>();
        builder.Services.AddSingleton<TraceViewModel>();
        builder.Services.AddSingleton<SendViewModel>();
        builder.Services.AddSingleton<DbcViewModel>();
        builder.Services.AddSingleton<SignalViewModel>();
        builder.Services.AddSingleton<StatsViewModel>();

        // Windows: AppShell is a WPF Window whose ctor requires an STA thread
        // (xunit's MTA threadpool cannot instantiate it). Register via a
        // factory that the host resolves on demand; production callers must
        // resolve AppShell from the STA thread (App.OnStartup qualifies).
        // The factory wires the VM via DataContext so XAML bindings resolve.
        builder.Services.AddSingleton<AppShell>(sp => new AppShell
        {
            DataContext = sp.GetRequiredService<AppShellViewModel>()
        });

        // Task 13: hosted service that wires the App-layer sinks
        // (TraceService + BusStatisticsCollector) into ChannelRouter at
        // host startup. Closes the Task 12 gap where the two were
        // registered as singletons but never connected.
        builder.Services.AddHostedService<SinkWiringService>();
        return builder.Build();
    }
}
