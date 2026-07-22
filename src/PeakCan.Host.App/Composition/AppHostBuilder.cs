using System.Globalization;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Path;
using PeakCan.Host.Core.Replay;
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
/// <remarks>
/// v1.3.1 PATCH Item 3: <see cref="AppHostBuilder"/> is an instance class
/// (not static) so it can carry optional configuration state across
/// fluent builder method calls. v1.3.0 MINOR Item 5 introduced
/// <see cref="WithUdsSecurityLockoutConfig"/>, the first fluent setter
/// requiring per-builder state. Future setters will follow the same
/// pattern.
/// <para>
/// <b>Lifecycle:</b> create one builder per application instance. Call
/// <see cref="Build"/> exactly once. The returned <see cref="IHost"/>
/// owns the Serilog lifetime and the DI container; dispose the host
/// (not the builder) when the app shuts down. Do not reuse a builder
/// after <see cref="Build"/> has been called.
/// </para>
/// <para>
/// <b>Pattern alignment:</b> follows the
/// <see href="https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host">
/// Microsoft.Extensions.Hosting IHost builder pattern</see>. The fluent
/// <c>With*</c> setters configure optional services; <see cref="Build"/>
/// resolves them into the DI container and starts the host. The DI
/// factory branches on optional state (e.g.
/// <c>_udsSecurityLockoutConfig is { } lockoutConfig</c>) to preserve
/// the default policy for legacy callers that do not invoke the
/// corresponding <c>With*</c> setter.
/// </para>
/// </remarks>
public partial class AppHostBuilder
{
    /// <summary>
    /// PEAK PCAN-USB FD first-channel handle. Per the inline amendment to
    /// Task 12, MVP probes a single hardcoded handle and does not
    /// enumerate; v1.1 will add multi-channel enumeration.
    /// </summary>
    public const ushort PcanUsbFdFirstHandle = 0x51;

    // v1.3.0 MINOR Item 5: optional UDS SecurityAccess lockout policy.
    // Set via WithUdsSecurityLockoutConfig; null means use the default
    // (UdsSecurityLockoutConfig.Default = 3 attempts / 5 s) inside the
    // UdsClient ctor.
    private PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig? _udsSecurityLockoutConfig;

    /// <summary>
    /// v1.3.0 MINOR Item 5: configure the UDS SecurityAccess lockout
    /// policy. Must be called before <see cref="Build"/>.
    /// <para>
    /// When this builder method is not called, the default policy
    /// (<see cref="PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig.Default"/>:
    /// 3 attempts / 5 s) is used. This preserves backward compatibility
    /// with v1.2.x callers.
    /// </para>
    /// </summary>
    /// <param name="config">Lockout policy (MaxAttempts + LockoutDuration).</param>
    /// <returns>The same builder, for fluent chaining.</returns>
    public AppHostBuilder WithUdsSecurityLockoutConfig(PeakCan.Host.Core.Uds.UdsSecurityLockoutConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _udsSecurityLockoutConfig = config;
        return this;
    }


    public IHost Build()
    {
        // === Flow A: Logging setup extracted to AppHostBuilder/LoggingFlow.cs (W11 Task 1) ===
        ConfigureLoggingAndBuilder(out var builder);

        // v1.5.0 MINOR: expose the host's IConfiguration as a singleton so
        // the AppShellViewModel can persist SelectedChannel to
        // Channel:SelectedHandle in appsettings.json. Host.CreateApplicationBuilder
        // already populates builder.Configuration with appsettings.json +
        // environment variables + command line.
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);


        // === Flow B: Core infrastructure extracted to AppHostBuilder/CoreInfrastructureFlow.cs (W11 Task 2) ===
        RegisterCoreInfrastructure(builder.Services);

        // App services
        // === Flow C: App services extracted to AppHostBuilder/AppServicesFlow.cs (W11 Task 3) ===
        RegisterAppServices(builder.Services);

        // === Flow D: ViewModels batch 1 extracted to AppHostBuilder/ViewModelsBatch1Flow.cs (W11 Task 4) ===
        RegisterViewModelsBatch1(builder.Services);


        // === Flow E: ViewModels batch 2 (Range A: TraceViewer section) extracted to AppHostBuilder/ViewModelsBatch2Flow.cs (W11 Task 5) ===
        RegisterViewModelsBatch2(builder.Services);

        // v0.7.0: file dialog abstraction for testability.
        builder.Services.AddSingleton<PeakCan.Host.Core.IFileDialogService,
                                       PeakCan.Host.App.Services.WpfFileDialogService>();
        // M11: DBC lookup + signal decode runs off the SDK read thread on
        // its own worker. Registered as both a singleton (so SinkWiringService
        // gets the same instance the host starts) and a hosted service
        // (so BackgroundService.StartAsync fires the worker loop).
        // v1.2.11 PATCH Item 2: factory takes TraceViewModel for fan-out
        // (worker fills entry.Decoded after looking up PendingDecode).
        // v1.2.12 PATCH Item 11: factory now also takes ILogger so OnError
        // is observable in Release builds.
        builder.Services.AddSingleton<DbcDecodeBackgroundService>(sp =>
            new DbcDecodeBackgroundService(
                sp.GetRequiredService<DbcService>(),
                sp.GetRequiredService<SignalViewModel>(),
                sp.GetRequiredService<TraceViewModel>(),
                sp.GetRequiredService<ILogger<DbcDecodeBackgroundService>>()));
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
            // v1.7.0 MINOR Item 1: V8 isolate resource caps.
            var scriptEngineOptions = sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngineOptions>();
            // ScriptUtilities will be resolved lazily to break the cycle.
            PeakCan.Host.App.Services.Scripting.ScriptUtilities? utilities = null;
            var engine = new PeakCan.Host.App.Services.Scripting.ScriptEngine(
                logger, canApi, dbcApi, null, scriptEngineOptions);
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
            // v1.6.5 PATCH Item 1: IsoTpLayer IS EXEMPT from rate-limit.
            // ISO 15765-2 has its own STmin pacing (consecutive-frame
            // transmit timing) that the protocol layer enforces; gating
            // it via the rate-limit decorator would break the transport
            // state machine. Inject CoreSendService (raw) directly.
            var sendService = sp.GetRequiredService<CoreSendService>();
            // v1.2.12 PATCH Item 2: async send callback. The previous
            // `.AsTask().Wait()` blocked the SDK read thread and deadlocked
            // the whole UDS diagnostic surface when SendService hung.
            // ConfigureAwait(false) avoids STA capture on the WPF UI thread;
            // exceptions are logged and swallowed inside the layer.
            var isoLogger = sp.GetRequiredService<ILogger<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>>();
            return new PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer(config, async frame =>
            {
                try
                {
                    await sendService.SendAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is PeakCan.Host.Core.Uds.IsoTp.IsoTpSendFailedException))
                {
                    // v1.2.13 PATCH Item 5: the layer's SendCanFrameAsync now
                    // throws IsoTpSendFailedException itself (after logging
                    // via LogIsoTpSendFailed). Skip the duplicate log here
                    // so each send failure is recorded exactly once (id
                    // 3001). The `when` filter is defense-in-depth for the
                    // (rare) case where SendService.SendAsync itself raises
                    // an IsoTpSendFailedException that the layer has not
                    // seen.
                    PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer.LogIsoTpSendFailed(
                        isoLogger, ex, frame.Id.Raw);
                }
            }, isoLogger);
        });
        // v1.1.0: SecurityAccess KeyProvider default. OEM overrides this at deploy time.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm, PeakCan.Host.Core.Uds.PlaceholderKeyAlgorithm>();
        // v1.1.0: DID + Routine databases (load from %APPDATA%\PeakCan.Host\ on construction).
        // v1.6.10 PATCH Item 2: factory wires PathOptions so the 3-arg ctor
        // (Task 5) receives the config-driven allowlist instead of the
        // hardcoded Default.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DidDatabase>(sp =>
            new PeakCan.Host.Core.Uds.Database.DidDatabase(
                PeakCan.Host.Core.Uds.Database.DidDatabaseDefaults.DefaultJsonPath,
                sp.GetRequiredService<ILogger<PeakCan.Host.Core.Uds.Database.DidDatabase>>(),
                sp.GetRequiredService<PathOptions>()));
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.RoutineDatabase>(sp =>
            new PeakCan.Host.Core.Uds.Database.RoutineDatabase(
                PeakCan.Host.Core.Uds.Database.RoutineDatabaseDefaults.DefaultJsonPath,
                sp.GetRequiredService<ILogger<PeakCan.Host.Core.Uds.Database.RoutineDatabase>>(),
                sp.GetRequiredService<PathOptions>()));
        // v1.1.0: UdsClient now requires an IKeyDerivationAlgorithm via the 3-arg ctor.
        // v1.2.13 PATCH Item 2: also pass ILogger<UdsSession> so S3 keepalive
        // failures are observable in production (logger-aware ctor was added
        // in v1.2.12 but never wired — this closes the known-deferred item).
        // v1.3.0 MINOR Item 5: when WithUdsSecurityLockoutConfig was called,
        // thread the policy through the new lockout-config ctor overload;
        // otherwise fall through to the legacy 3-arg ctor (defaults preserved).
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsClient>(sp =>
        {
            var isoTp = sp.GetRequiredService<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>();
            var keyAlgorithm = sp.GetRequiredService<PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm>();
            var sessionLogger = sp.GetService<ILogger<PeakCan.Host.Core.Uds.UdsSession>>();
            if (_udsSecurityLockoutConfig is { } lockoutConfig)
            {
                return new PeakCan.Host.Core.Uds.UdsClient(
                    isoTp, keyAlgorithm, lockoutConfig,
                    timer: null, sessionLogger: sessionLogger);
            }
            return new PeakCan.Host.Core.Uds.UdsClient(isoTp, keyAlgorithm, sessionLogger: sessionLogger);
        });
        // v1.2.0: 4-panel orchestrator holds Session/Did/Routine/Dtc panel VMs;
        // each panel VM is registered as a singleton below and DI auto-resolves
        // the new UdsViewModel ctor (SessionPanelViewModel, DidPanelViewModel,
        // RoutinePanelViewModel, DtcPanelViewModel).
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.SessionPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DidPanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.RoutinePanelViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DtcPanelViewModel>();
        // C4 flashing pipeline: the per-flash secondary stack factory (resolves the shared
        // CoreSendService/ChannelRouter/UdsTimer/loggers once) + the Flashing-tab panel VM
        // (owns the stack lifecycle + pipeline execution). Registered as singletons so the
        // FlashPanelViewModel holds a stable factory but builds a FRESH stack per Start.
        // Registered BEFORE UdsViewModel so the orchestrator's ctor can resolve it (6th panel).
        //
        // Both ctors are `internal` (the factory + the VM expose App-internal seam contracts —
        // ISecondaryFlashStackFactory — and a public ctor taking internal params would trip
        // CS0051). DI's CallSiteFactory only walks PUBLIC ctors, so the registrations use
        // explicit factory lambdas (`sp => new ...`) that reach the internal ctor in-assembly —
        // the same pattern used for IsoTpLayer (line 181) and UdsClient (line 244).
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.ISecondaryFlashStackFactory>(sp =>
            new PeakCan.Host.App.Composition.SecondaryFlashStackFactory(
                sp.GetRequiredService<PeakCan.Host.App.Composition.CoreSendService>(),
                sp.GetRequiredService<PeakCan.Host.Infrastructure.Channel.ChannelRouter>(),
                sp.GetRequiredService<PeakCan.Host.Core.Uds.UdsTimer>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.Core.Uds.UdsSession>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Composition.SecondaryFlashStack>>()));
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel>(sp =>
            new PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel(
                sp.GetRequiredService<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.ISecondaryFlashStackFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel>>()));
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.UdsViewModel>();

        // v2.0.0 MINOR: ODX-D DIAG-LAYER importer. In-memory databases +
        // Core parser/persistence plus App-layer service + VM glue.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DtcDatabase>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Odx.OdxParser>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Odx.PdxReader>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.IOdxImportService,
            PeakCan.Host.App.Services.OdxImportService>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.OdxImportViewModel>();

        // ViewModels
        // v1.5.0 MINOR: AppShellViewModel ctor takes an optional IConfiguration
        // for SelectedChannel persistence. Wire via factory so the DI
        // container resolves the host's IConfiguration; this keeps the
        // existing parameterless AddSingleton call sites (test fakes) working.
        builder.Services.AddSingleton<AppShellViewModel>(sp => new AppShellViewModel(
            sp.GetRequiredService<ChannelRouter>(),
            sp.GetRequiredService<ILogger<AppShellViewModel>>(),
            sp.GetRequiredService<TraceViewModel>(),
            sp.GetRequiredService<SendService>(),
            sp.GetRequiredService<IChannelProbe>(),
            sp.GetRequiredService<IChannelFactory>(),
            sp.GetRequiredService<DbcViewModel>(),
            sp.GetRequiredService<SendViewModel>(),
            sp.GetRequiredService<SignalViewModel>(),
            sp.GetRequiredService<StatsViewModel>(),
            sp.GetRequiredService<ScriptViewModel>(),
            sp.GetRequiredService<UdsViewModel>(),
            // v3.50.1 PATCH-A: RecordViewModel wiring restored.
            sp.GetRequiredService<RecordViewModel>(),
            sp.GetRequiredService<ReplayViewModel>(),
            sp.GetRequiredService<PeakCan.Host.App.ViewModels.MultiFrameSendViewModel>(),
            sp.GetRequiredService<TraceViewerViewModel>(),
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.RecentSessionsService>(),
            sp.GetRequiredService<PeakCan.Host.Core.IFileDialogService>(),
            // v3.10.0 MINOR T1 (C1): IMessageBoxPrompt seam — replaces
            // the direct MessageBox.Show calls in OpenSessionAsync /
            // OpenRecentSessionAsync (WPFMessageBoxPrompt wired by DI
            // registration above; tests inject Substitute.For<...>()).
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>(),
            sp.GetService<PeakCan.Host.Core.IChannelEnumerator>(),
            sp.GetRequiredService<IConfiguration>()));

        // === Flow E: ViewModels batch 2 (Range B: Trace/Send/Dbc/SignalChart/Signal/Stats/Script) extracted to AppHostBuilder/ViewModelsBatch2Flow.cs (W11 Task 5) ===
        RegisterViewModelsBatch2(builder.Services);

        // === Flow G: Window + hosted services extracted to AppHostBuilder/WindowAndHostedServicesFlow.cs (W11 Task 6 — LAST extraction) ===
        RegisterWindowAndHostedServices(builder.Services);

        return builder.Build();
    }
}
