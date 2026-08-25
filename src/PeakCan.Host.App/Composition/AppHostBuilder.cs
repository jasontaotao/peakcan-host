using System.Globalization;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Path;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL;
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
    private PeakCan.HIL.Core.Uds.UdsSecurityLockoutConfig? _udsSecurityLockoutConfig;

    /// <summary>
    /// v1.3.0 MINOR Item 5: configure the UDS SecurityAccess lockout
    /// policy. Must be called before <see cref="Build"/>.
    /// <para>
    /// When this builder method is not called, the default policy
    /// (<see cref="PeakCan.HIL.Core.Uds.UdsSecurityLockoutConfig.Default"/>:
    /// 3 attempts / 5 s) is used. This preserves backward compatibility
    /// with v1.2.x callers.
    /// </para>
    /// </summary>
    /// <param name="config">Lockout policy (MaxAttempts + LockoutDuration).</param>
    /// <returns>The same builder, for fluent chaining.</returns>
    public AppHostBuilder WithUdsSecurityLockoutConfig(PeakCan.HIL.Core.Uds.UdsSecurityLockoutConfig config)
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

        // P2-6: AppShell 布局持久化 store（右栏宽 + 主右 tab 选中项）。镜像
        // WindowStateStore 的单例接线（AppServicesFlow.cs 的
        // AddSingleton<WindowStateStore>）。注入到 AppShell 构造处在
        // WindowAndHostedServicesFlow.cs 的 AppShell 工厂（App.OnStartup 经
        // DI 解析 AppShell，两个 store 都会注入；LoadAsync 也在工厂里 fire-and-forget）。
        builder.Services.AddSingleton<LayoutStateStore>();

        // Phase 1 重构: 绑定 Llm 配置到 LlmOptions。
        // 所有消费者 (OpenAiCompatibleChatProvider, HilAnalysisService) 读此实例。
        builder.Services.Configure<PeakCan.HIL.Core.Analysis.LlmOptions>(
            builder.Configuration.GetSection("Llm"));

        // === Flow D: ViewModels batch 1 extracted to AppHostBuilder/ViewModelsBatch1Flow.cs (W11 Task 4) ===
        RegisterViewModelsBatch1(builder.Services);


        // === Flow E: ViewModels batch 2 (Range A: TraceViewer section) extracted to AppHostBuilder/ViewModelsBatch2Flow.cs (W11 Task 5) ===
        RegisterViewModelsBatch2(builder.Services);

        // v0.7.0: file dialog abstraction for testability.
        builder.Services.AddSingleton<PeakCan.HIL.Core.IFileDialogService,
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

        // v1.0.0: Scripting engine. ScriptEngine → ScriptUtilities 是单向依赖
        // (CreateEngineFlow 暴露 log/warn/error 给 JS)；反向通过 Lazy<ScriptUtilities>
        // 延迟解析，从 ctor 层面打破循环，替代旧的反射 field 注入。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptEngine>(sp =>
            new PeakCan.Host.App.Services.Scripting.ScriptEngine(
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptEngine>>(),
                sp.GetService<PeakCan.Host.App.Services.Scripting.CanApi>(),
                sp.GetService<PeakCan.Host.App.Services.Scripting.DbcApi>(),
                new Lazy<PeakCan.Host.App.Services.Scripting.ScriptUtilities>(
                    () => sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptUtilities>()),
                // v1.7.0 MINOR Item 1: V8 isolate resource caps.
                sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngineOptions>()));
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.CanApi>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.DbcApi>();
        // IScriptOutputSink forward 到 ScriptEngine（单一实现）。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.IScriptOutputSink>(sp =>
            sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngine>());
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptUtilities>();

        // v1.1.0: UDS diagnostic stack.
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.UdsTimer>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer>(sp =>
        {
            var config = new PeakCan.HIL.Core.Uds.IsoTp.CanIdConfig
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
            var isoLogger = sp.GetRequiredService<ILogger<PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer>>();
            return new PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer(config, async frame =>
            {
                try
                {
                    await sendService.SendAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is PeakCan.HIL.Core.Uds.IsoTp.IsoTpSendFailedException))
                {
                    // v1.2.13 PATCH Item 5: the layer's SendCanFrameAsync now
                    // throws IsoTpSendFailedException itself (after logging
                    // via LogIsoTpSendFailed). Skip the duplicate log here
                    // so each send failure is recorded exactly once (id
                    // 3001). The `when` filter is defense-in-depth for the
                    // (rare) case where SendService.SendAsync itself raises
                    // an IsoTpSendFailedException that the layer has not
                    // seen.
                    PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer.LogIsoTpSendFailed(
                        isoLogger, ex, frame.Id.Raw);
                }
            }, isoLogger);
        });
        // v1.1.0: SecurityAccess KeyProvider default. OEM overrides this at deploy time.
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.IKeyDerivationAlgorithm, PeakCan.HIL.Core.Uds.PlaceholderKeyAlgorithm>();
        // v1.1.0: DID + Routine databases (load from %APPDATA%\PeakCan.Host\ on construction).
        // v1.6.10 PATCH Item 2: factory wires PathOptions so the 3-arg ctor
        // (Task 5) receives the config-driven allowlist instead of the
        // hardcoded Default.
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.Database.DidDatabase>(sp =>
            new PeakCan.HIL.Core.Uds.Database.DidDatabase(
                PeakCan.HIL.Core.Uds.Database.DidDatabaseDefaults.DefaultJsonPath,
                sp.GetRequiredService<ILogger<PeakCan.HIL.Core.Uds.Database.DidDatabase>>(),
                sp.GetRequiredService<PathOptions>()));
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.Database.RoutineDatabase>(sp =>
            new PeakCan.HIL.Core.Uds.Database.RoutineDatabase(
                PeakCan.HIL.Core.Uds.Database.RoutineDatabaseDefaults.DefaultJsonPath,
                sp.GetRequiredService<ILogger<PeakCan.HIL.Core.Uds.Database.RoutineDatabase>>(),
                sp.GetRequiredService<PathOptions>()));
        // v1.1.0: UdsClient now requires an IKeyDerivationAlgorithm via the 3-arg ctor.
        // v1.2.13 PATCH Item 2: also pass ILogger<UdsSession> so S3 keepalive
        // failures are observable in production (logger-aware ctor was added
        // in v1.2.12 but never wired — this closes the known-deferred item).
        // v1.3.0 MINOR Item 5: when WithUdsSecurityLockoutConfig was called,
        // thread the policy through the new lockout-config ctor overload;
        // otherwise fall through to the legacy 3-arg ctor (defaults preserved).
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.UdsClient>(sp =>
        {
            var isoTp = sp.GetRequiredService<PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer>();
            var keyAlgorithm = sp.GetRequiredService<PeakCan.HIL.Core.Uds.IKeyDerivationAlgorithm>();
            var sessionLogger = sp.GetService<ILogger<PeakCan.HIL.Core.Uds.UdsSession>>();
            if (_udsSecurityLockoutConfig is { } lockoutConfig)
            {
                return new PeakCan.HIL.Core.Uds.UdsClient(
                    isoTp, keyAlgorithm, lockoutConfig,
                    timer: null, sessionLogger: sessionLogger);
            }
            return new PeakCan.HIL.Core.Uds.UdsClient(isoTp, keyAlgorithm, sessionLogger: sessionLogger);
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
                sp.GetRequiredService<PeakCan.HIL.Core.Uds.UdsTimer>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.HIL.Core.Uds.IsoTp.IsoTpLayer>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.HIL.Core.Uds.UdsSession>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Composition.SecondaryFlashStack>>()));
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel>(sp =>
            new PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel(
                sp.GetRequiredService<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.ISecondaryFlashStackFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel>>(),
                sp.GetRequiredService<PeakCan.HIL.Core.IFileDialogService>(),
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.FlashConfigurationService>()));
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.UdsViewModel>();

        // Sprint 3: HIL test runner (Infrastructure implementation, Core interface)
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.IHilRunnerService, Infrastructure.HIL.HilRunnerService>();
        // Spec v3 §3.4: HilViewModel 的 connectedChannels 提供者由 AppShellViewModel
        // 构造时注入（AppShell 单例持有连接状态；DI factory 引 shell 会形成
        // AppShell ⇄ HilViewModel 循环解析死锁——恢复普通 transient 注册）。
        builder.Services.AddTransient<ViewModels.HilViewModel>();
        builder.Services.AddSingleton<ViewModels.EcuScriptEditorViewModel>();
        // Phase 7 Unit C: HIL HTML report service (WPF 面板消费出口，单例无状态)。
        builder.Services.AddSingleton<Infrastructure.HIL.Reporting.IHilReportService,
            Infrastructure.HIL.Reporting.HilReportService>();

        // v2.0.0 MINOR: ODX-D DIAG-LAYER importer. In-memory databases +
        // Core parser/persistence plus App-layer service + VM glue.
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.Database.DtcDatabase>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.Odx.OdxParser>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.Uds.Odx.PdxReader>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.IOdxImportService,
            PeakCan.Host.App.Services.OdxImportService>();
        // Phase 2 (spec §8): ODX-derived flash configuration provider.
        // FlashConfigurationService is a mutable singleton — OdxImportService
        // calls UpdateFromOdx() after each import; FlashPanelViewModel reads it.
        builder.Services.AddSingleton<PeakCan.Host.App.Services.FlashConfigurationService>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.IFlashConfigurationProvider>(sp =>
            sp.GetRequiredService<PeakCan.Host.App.Services.FlashConfigurationService>());
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
            // v3.x (会话状态剥离 Task 2): AppShell 不再注入 TraceViewerViewModel，
            // 改注入 ITraceSessionService（会话命令） + Func 工厂（开窗时懒解析 VM）。
            // v3.x Task 5 final: VM 已 transient（Task 3 完成），工厂每次开窗都解析
            // 新实例；会话状态已剥离到 singleton service，窗口关闭即丢弃窗口级状态。
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.ITraceSessionService>(),
            () => sp.GetRequiredService<TraceViewerViewModel>(),
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.RecentSessionsService>(),
            sp.GetRequiredService<PeakCan.HIL.Core.IFileDialogService>(),
            // v3.10.0 MINOR T1 (C1): IMessageBoxPrompt seam — replaces
            // the direct MessageBox.Show calls in OpenSessionAsync /
            // OpenRecentSessionAsync (WPFMessageBoxPrompt wired by DI
            // registration above; tests inject Substitute.For<...>()).
            sp.GetRequiredService<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>(),
            // Sprint 3: HIL testing panel VM
            sp.GetRequiredService<ViewModels.HilViewModel>(),
            sp.GetRequiredService<ViewModels.EcuScriptEditorViewModel>(),
            sp.GetService<PeakCan.HIL.Core.IChannelEnumerator>(),
            sp.GetRequiredService<IConfiguration>(),
            // P1-2: all device providers for the connection-settings panel.
            deviceProviders: sp.GetServices<PeakCan.HIL.Core.Devices.ICanDeviceProvider>(),
            // P0-3: shared secondary-window host (DI singleton).
            windowHost: sp.GetRequiredService<PeakCan.Host.App.Services.Ui.WindowHostService>()));

        // === Flow E: ViewModels batch 2 (Range B: Trace/Send/Dbc/SignalChart/Signal/Stats/Script) extracted to AppHostBuilder/ViewModelsBatch2Flow.cs (W11 Task 5) ===
        RegisterViewModelsBatch2(builder.Services);

        // === Flow G: Window + hosted services extracted to AppHostBuilder/WindowAndHostedServicesFlow.cs (W11 Task 6 — LAST extraction) ===
        RegisterWindowAndHostedServices(builder.Services);

        return builder.Build();
    }
}
