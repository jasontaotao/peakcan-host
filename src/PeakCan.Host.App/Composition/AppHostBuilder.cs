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
        // TraceService is a BackgroundService; its 50ms drain loop lives in
        // ExecuteAsync and only fires when the host starts it. Without this
        // AddHostedService line, frames pile up in the bounded channel and
        // TotalFrameCount stays 0 even though fan-out delivers them. Same
        // bug pattern as the IHost.StartAsync miss in App.xaml.cs.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TraceService>());
        builder.Services.AddSingleton<SendService>();
        builder.Services.AddSingleton<DbcService>();
        builder.Services.AddSingleton<StatisticsService>();
        // StatisticsService is a BackgroundService; its 1Hz snapshot loop
        // needs IHostedService registration too. Without this, Stats view
        // never refreshes — matches TraceService bug pattern.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StatisticsService>());
        // v0.5.0: frame recording (ASC/CSV) and cyclic send.
        builder.Services.AddSingleton<RecordService>();
        builder.Services.AddSingleton<CyclicSendService>();
        // v1.2.11 PATCH Item 3: register cyclic service as its interface
        // so SendViewModel can be tested with a fake via ICyclicSendService.
        builder.Services.AddSingleton<ICyclicSendService>(sp =>
            sp.GetRequiredService<CyclicSendService>());
        // v1.2.11 PATCH Item 5: named-frame library persistence.
        builder.Services.AddSingleton<SendFrameLibrary>();

        // v0.7.0: file dialog abstraction for testability.
        builder.Services.AddSingleton<PeakCan.Host.Core.IFileDialogService,
                                       PeakCan.Host.App.Services.WpfFileDialogService>();
        // M11: DBC lookup + signal decode runs off the SDK read thread on
        // its own worker. Registered as both a singleton (so SinkWiringService
        // gets the same instance the host starts) and a hosted service
        // (so BackgroundService.StartAsync fires the worker loop).
        // v1.2.11 PATCH Item 2: factory takes TraceViewModel for fan-out
        // (worker fills entry.Decoded after looking up PendingDecode).
        builder.Services.AddSingleton<DbcDecodeBackgroundService>(sp =>
            new DbcDecodeBackgroundService(
                sp.GetRequiredService<DbcService>(),
                sp.GetRequiredService<SignalViewModel>(),
                sp.GetRequiredService<TraceViewModel>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DbcDecodeBackgroundService>());

        // v1.0.0: Scripting engine.
        // ScriptEngine has a circular dependency with ScriptUtilities (ScriptEngine
        // needs ScriptUtilities for logging, ScriptUtilities needs ScriptEngine for
        // output routing). Break the cycle by registering ScriptEngine first with a
        // factory that lazily resolves ScriptUtilities.
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptEngine>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptEngine>>();
            var canApi = sp.GetService<PeakCan.Host.App.Services.Scripting.CanApi>();
            var dbcApi = sp.GetService<PeakCan.Host.App.Services.Scripting.DbcApi>();
            // ScriptUtilities will be resolved lazily to break the cycle.
            PeakCan.Host.App.Services.Scripting.ScriptUtilities? utilities = null;
            var engine = new PeakCan.Host.App.Services.Scripting.ScriptEngine(logger, canApi, dbcApi, null);
            // Now create ScriptUtilities with the engine reference.
            utilities = new PeakCan.Host.App.Services.Scripting.ScriptUtilities(
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptUtilities>>(),
                engine);
            // Update the engine's utilities field via reflection.
            var field = typeof(PeakCan.Host.App.Services.Scripting.ScriptEngine)
                .GetField("_utilities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(engine, utilities);
            return engine;
        });
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.CanApi>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.DbcApi>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptUtilities>(sp =>
        {
            var engine = sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngine>();
            return new PeakCan.Host.App.Services.Scripting.ScriptUtilities(
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptUtilities>>(),
                engine);
        });

        // v1.1.0: UDS diagnostic stack.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsTimer>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>(sp =>
        {
            var config = new PeakCan.Host.Core.Uds.IsoTp.CanIdConfig
            {
                RequestId = 0x7E0,  // Default UDS physical request ID
                ResponseId = 0x7E8  // Default UDS physical response ID
            };
            var sendService = sp.GetRequiredService<SendService>();
            return new PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer(config, frame =>
            {
                // Fire-and-forget send (simplified for MVP)
                sendService.SendAsync(frame).AsTask().Wait();
            });
        });
        // v1.1.0: SecurityAccess KeyProvider default. OEM overrides this at deploy time.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm, PeakCan.Host.Core.Uds.PlaceholderKeyAlgorithm>();
        // v1.1.0: DID + Routine databases (load from %APPDATA%\PeakCan.Host\ on construction).
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DidDatabase>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.RoutineDatabase>();
        // v1.1.0: UdsClient now requires an IKeyDerivationAlgorithm via the 3-arg ctor.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsClient>(sp =>
        {
            var isoTp = sp.GetRequiredService<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>();
            var keyAlgorithm = sp.GetRequiredService<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm>();
            return new PeakCan.Host.Core.Uds.UdsClient(isoTp, keyAlgorithm);
        });
        // v1.2.0: 4-panel orchestrator holds Session/Did/Routine/Dtc panel VMs;
        // each panel VM is registered as a singleton below and DI auto-resolves
        // the new UdsViewModel ctor (SessionPanelViewModel, DidPanelViewModel,
        // RoutinePanelViewModel, DtcPanelViewModel).
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.SessionPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DidPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.RoutinePanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DtcPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.UdsViewModel>();

        // ViewModels
        builder.Services.AddSingleton<AppShellViewModel>();
        builder.Services.AddSingleton<TraceViewModel>();
        builder.Services.AddSingleton<SendViewModel>();
        builder.Services.AddSingleton<DbcViewModel>();
        // v0.8.0: signal chart VM must be registered before SignalViewModel
        // (SignalViewModel depends on it via constructor injection).
        builder.Services.AddSingleton<SignalChartViewModel>();
        builder.Services.AddSingleton<SignalViewModel>();
        builder.Services.AddSingleton<StatsViewModel>();
        builder.Services.AddSingleton<ScriptViewModel>();

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
