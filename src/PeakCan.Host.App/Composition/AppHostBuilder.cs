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
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.Statistics;
using Serilog;
using PeakCan.Host.Core.Path;
using PeakCan.Host.Core;

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

        // M2.4b（spec §5-D6.7）：SecOC 旁路 verdict 表单例（App 侧 SecOC 通道
        // 接线在 Phase 3 落地；本单例先行供徽标 join / 断开清理钩子使用）。
        builder.Services.AddSingleton<PeakCan.Host.Infrastructure.Channel.SecOc.SecOcVerdictTable>();

        // SecOC App 接线（2026-09-16 plan）：徽章 joiner 单例 + 连接路径 PDU
        // provider。provider 读 App 固定配置并经 CLI 同款 loader 校验（keyId
        // 缺失 fail-loud）。AppShellViewModel 为显式工厂注册，实参在工厂内
        // GetRequiredService 转发（fix round 1：工厂不回填可选参数）。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.SecOc.SecOcBadgeJoiner>();
        // 缺口 1a（2026-09-17）：per-handle provider——连接时按通道 handle 取对应
        // PDU 配置（entry.Handle 空 = 全局兜底）。缺失配置返回 null（零回归）；配
        // 置错误（keyId 缺失/畸形）fail-loud。
        builder.Services.AddSingleton<Func<ushort, IReadOnlyDictionary<uint, PeakCan.Host.Infrastructure.Channel.SecOc.SecOcPduConfig>?>>(
            _ => handle => PeakCan.Host.App.Services.SecOc.SecOcAppConfigStore.LoadForConnectPath(handle));

        // v1.0.0: Scripting engine. P1-2（2026-09-06，Lazy<T> 清零）：输出走
        // ScriptOutputHub 单向流（ScriptUtilities → hub → ScriptEngine 转发到
        // OutputReceived），依赖图无环——ScriptEngine 直接 ctor 持有 ScriptUtilities。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptOutputHub>();
        // IScriptOutputSink 由 hub 承担（ScriptUtilities 的输出通道）。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.IScriptOutputSink>(sp =>
            sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptOutputHub>());
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptUtilities>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptEngine>(sp =>
            new PeakCan.Host.App.Services.Scripting.ScriptEngine(
                sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptEngine>>(),
                sp.GetService<PeakCan.Host.App.Services.Scripting.CanApi>(),
                sp.GetService<PeakCan.Host.App.Services.Scripting.DbcApi>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptUtilities>(),
                // v1.7.0 MINOR Item 1: V8 isolate resource caps.
                sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngineOptions>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptOutputHub>()));
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.CanApi>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.DbcApi>();

        // v1.1.0: UDS diagnostic stack.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.UdsTimer>();
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer>(sp =>
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

        // J1939TP stack：每应用一个 singleton（跟随 CoreSendService 的活动通道模型；
        // 多通道扩展锚点保留——层角色无关，后续按通道建实例时移到 per-channel 注册点）。
        builder.Services.AddSingleton<PeakCan.Host.Core.J1939.J1939TpLayer>(sp =>
        {
            var sendService = sp.GetRequiredService<CoreSendService>();
            var j1939Logger = sp.GetRequiredService<ILogger<PeakCan.Host.Core.J1939.J1939TpLayer>>();
            return new PeakCan.Host.Core.J1939.J1939TpLayer(
                async (frame, ct) =>
                {
                    try
                    {
                        return await sendService.SendAsync(frame, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        PeakCan.Host.Core.J1939.J1939TpLayer.LogSendFailed(j1939Logger, ex, frame.Id.Raw);
                        return PeakCan.HIL.Core.Result<Unit>.Fail(
                            PeakCan.HIL.Core.ErrorCode.InvalidState, ex.Message);
                    }
                },
                new PeakCan.Host.Core.J1939.J1939TpOptions(),
                j1939Logger);
        });
        builder.Services.AddSingleton<PeakCan.Host.App.Composition.J1939TpSinkAdapter>();
        // J1939TP 在线会话异常（SessionEvent）→ 实时 Trace 面板：IHostedService 随宿主
        // 启动订阅、停止退订（详见 J1939TpSessionEventSink 的类注释）。不用 SinkWiringService
        // 构造依赖——其测试是手搭最小 DI 图，加构造参需同步改测试。
        builder.Services.AddHostedService<PeakCan.Host.App.Composition.J1939TpSessionEventSink>();

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
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel>>(),
                sp.GetRequiredService<PeakCan.HIL.Core.IFileDialogService>(),
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<PeakCan.Host.App.Services.FlashConfigurationService>()));
        // Read-only communication-parameters panel: polls the diagnostic IsoTpLayer/UdsClient
        // singletons + the FlashPanelViewModel's in-flight secondary stack. Public ctor, so
        // plain registration suffices — UdsViewModel's optional ctor param auto-resolves to it.
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.TransportParamsViewModel>();
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.UdsViewModel>();


        // ================================================================
        // S3-T8：XCP tab 接线（spec D1）。四面板 + orchestrator 全 singleton
        //（运行状态跨 tab 切换保持，UdsViewModel 先例）。
        // ================================================================
        // D3 管线 sink singleton：卡片面板与采集面板共享同一实例（采集出站 → 卡片入队）。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Xcp.XcpCardPanelSink>();
        // 卡片面板：stalePeriod = 100 ms（T8 第 6 步 MVP 取舍：全局 3×30ms 阈值近似。
        // T5 评审移交"按对象实际节奏设置"——轮转表中更新周期 >30ms 的对象会被推迟
        // 标灰（宁迟勿误报）；逐对象周期接线留待 T11/后续任务，此处显式钉住全局口径。
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Xcp.XcpCardPanelViewModel>(sp =>
            new PeakCan.Host.App.ViewModels.Xcp.XcpCardPanelViewModel(
                sp.GetRequiredService<PeakCan.Host.App.Services.Xcp.XcpCardPanelSink>(),
                stalePeriod: TimeSpan.FromMilliseconds(100)));
        // T3 连接面板（T8 评审移交 T10 落地）：显式工厂——loadA2l 显式传
        // Core XcpA2lLoader.Load（D4 单源），防止未来注册 Func<string, XcpA2lLoadResult>
        // 时被可选参 auto-resolve 静默顶掉默认 loader；connectedChannels 解析
        // IConnectedChannelsSource singleton（Q2 已连接通道快照）。
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Xcp.XcpConnectionPanelViewModel>(sp =>
            new PeakCan.Host.App.ViewModels.Xcp.XcpConnectionPanelViewModel(
                loadA2l: PeakCan.Host.Core.Xcp.Capability.XcpA2lLoader.Load,
                connectedChannels: sp.GetRequiredService<PeakCan.Host.App.Services.IConnectedChannelsSource>()));
        // T6 归因面板：ctor 注入卡片面板即完成 GapObserved 接力订阅（Attach 幂等）。
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Xcp.XcpAttributionPanelViewModel>();
        // S4-T5（spec D4）：记录 sink singleton——MDF 记录文件落会话 recordings 目录。
        builder.Services.AddSingleton<PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSink>(sp =>
            new PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSink(
                new PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSinkOptions
                {
                    Directory = System.IO.Path.Combine(AppContext.BaseDirectory, "recordings"),
                }));
        // S4-T6（spec D6）：触发记录引擎 singleton——常驻环（广播第三子），
        // 触发落独立 xcp_trigger 文件；通道/快照取触发时刻关注集（运行期动态）。
        builder.Services.AddSingleton<PeakCan.Host.Core.Xcp.Record.XcpTriggerRecordEngine>(sp =>
            new PeakCan.Host.Core.Xcp.Record.XcpTriggerRecordEngine(
                new PeakCan.Host.Core.Xcp.Record.XcpTriggerRecordOptions
                {
                    Directory = System.IO.Path.Combine(AppContext.BaseDirectory, "recordings"),
                    ChannelProvider = () => sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpCardPanelViewModel>()
                        .Cards.Select(c => new PeakCan.Host.Core.Xcp.Record.MdfChannelSpec(c.Name, c.Contract.Unit)).ToList(),
                    SnapshotFactory = () => sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpRecordPanelViewModel>()
                        .BuildSnapshotFromConnection(),
                }));
        // S4-T6：脚本触发源（xcp-trigger: 出站口 → 触发引擎，spec D6 触发源 v0.2）。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.Xcp.XcpScriptTriggerSource>();
        // S4-T5（spec D4 广播装配）：采集出站 = 广播 sink（卡片 sink + 记录 sink + 触发环）。
        // 记录未启用时记录 sink 入队恒 no-op（未记录态直返）——零行为变化。
        builder.Services.AddSingleton(sp =>
            new PeakCan.Host.Core.Xcp.Record.XcpBroadcastSink(new PeakCan.Host.Core.Xcp.Receive.IXcpAcquisitionSink[]
            {
                sp.GetRequiredService<PeakCan.Host.App.Services.Xcp.XcpCardPanelSink>(),
                sp.GetRequiredService<PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSink>(),
                sp.GetRequiredService<PeakCan.Host.Core.Xcp.Record.XcpTriggerRecordEngine>(),
            }));
        // S4-T5 记录面板：显式工厂转发广播装配的记录 sink + 面板三件。
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Xcp.XcpRecordPanelViewModel>(sp =>
            new PeakCan.Host.App.ViewModels.Xcp.XcpRecordPanelViewModel(
                sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel>(),
                sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpCardPanelViewModel>(),
                sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpConnectionPanelViewModel>(),
                sp.GetRequiredService<PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSink>(),
                trigger: sp.GetRequiredService<PeakCan.Host.Core.Xcp.Record.XcpTriggerRecordEngine>()));
        // T7 采集面板：显式工厂转发连接面板 + 广播 sink（D4：卡片+记录 fan-out；
        // MS DI 工厂不回填可选参，漏转发即生产静默裸跑——AppShell SecOC 三件套同款教训）。
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel>(sp =>
            new PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel(
                sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpConnectionPanelViewModel>(),
                sp.GetRequiredService<PeakCan.Host.Core.Xcp.Record.XcpBroadcastSink>()));

        // Orchestrator：可空可选参 auto-resolve 四面板 singleton 原样组装。
        builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Xcp.XcpViewModel>();
        // T7b 评审 L-2：App 关闭路径必须真正等待 XcpAcquisitionPanelViewModel.StopAsync
        //（fire-and-forget 会丢 S2 quiesce 契约）。IHostedService.StopAsync 在
        // App.RunShutdownAsync 的 host.StopAsync（10s 上限）内被真正 await。
        builder.Services.AddHostedService(sp => new XcpAcquisitionShutdownService(
            sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel>()));
        // Sprint 3: HIL test runner (Infrastructure implementation, Core interface)
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.IHilRunnerService, Infrastructure.HIL.HilRunnerService>();
        // P1-2（2026-09-06）: 已连接通道快照源（无依赖 singleton，先于 shell/HilVM 解析，
        // 打破 AppShell⇄HilViewModel DI 环——HilViewModel 恢复 singleton 注册）。
        builder.Services.AddSingleton<PeakCan.Host.App.Services.IConnectedChannelsSource,
            PeakCan.Host.App.Services.ConnectedChannelsSource>();
        // Spec v3 §3.4: HilViewModel 恢复 singleton 注册（P1-2 2026-09-06：原为规避
        // AppShell⇄HilViewModel setter 注入环的 transient）。connectedChannels 工厂
        // 从 IConnectedChannelsSource 快照源取值（AppShell publish），DI 无环。
        builder.Services.AddSingleton<ViewModels.HilViewModel>(sp => new ViewModels.HilViewModel(
            sp.GetRequiredService<PeakCan.Host.Core.HIL.IHilRunnerService>(),
            sp.GetRequiredService<ILogger<ViewModels.HilViewModel>>(),
            sp.GetRequiredService<PeakCan.HIL.Core.IFileDialogService>(),
            sp.GetRequiredService<PeakCan.Host.Core.HIL.Analysis.IHilAnalysisService>(),
            sp.GetRequiredService<PeakCan.Host.Infrastructure.HIL.Reporting.IHilReportService>(),
            connectedChannels: () => sp.GetRequiredService<PeakCan.Host.App.Services.IConnectedChannelsSource>().Current,
            connectedChannelsSource: sp.GetRequiredService<PeakCan.Host.App.Services.IConnectedChannelsSource>(),
            trialRunService: sp.GetRequiredService<PeakCan.Host.Core.HIL.Contracts.ITrialRunService>(),
            preflightService: sp.GetRequiredService<PeakCan.Host.App.Services.HilPreflight.SuitePreflightService>(),
            runHistoryStore: sp.GetRequiredService<PeakCan.Host.App.Services.HilHistory.HilRunHistoryStore>()));
        builder.Services.AddSingleton<ViewModels.EcuScriptEditorViewModel>();
        // Phase 7 Unit C: HIL HTML report service (WPF 面板消费出口，单例无状态)。
        builder.Services.AddSingleton<Infrastructure.HIL.Reporting.IHilReportService,
            Infrastructure.HIL.Reporting.HilReportService>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.Contracts.ITrialRunService,
            Infrastructure.HIL.Environment.TrialRunService>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.HilPreflight.SuitePreflightService>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.HilPanel.HilPanelStateStore>();
        builder.Services.AddSingleton<PeakCan.Host.App.Services.HilHistory.HilRunHistoryStore>();

        // v2.0.0 MINOR: ODX-D DIAG-LAYER importer. In-memory databases +
        // Core parser/persistence plus App-layer service + VM glue.
        builder.Services.AddSingleton<PeakCan.Host.Core.Uds.Database.DtcDatabase>();
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
            sp.GetService<PeakCan.Host.Core.IChannelEnumerator>(),
            sp.GetRequiredService<IConfiguration>(),
            // P1-2: all device providers for the connection-settings panel.
            deviceProviders: sp.GetServices<PeakCan.Host.Core.Devices.ICanDeviceProvider>(),
            // P0-3: shared secondary-window host (DI singleton).
            windowHost: sp.GetRequiredService<PeakCan.Host.App.Services.Ui.WindowHostService>(),
            // P1-2（2026-09-06）: 已连接通道快照源（HilViewModel 消费）。
            connectedChannelsSource: sp.GetRequiredService<PeakCan.Host.App.Services.IConnectedChannelsSource>(),
            // 2026-09-06 设计层 MEDIUM：连接成功时更新总线负载分母（标称波特率）。
            busStats: sp.GetRequiredService<PeakCan.Host.Infrastructure.Statistics.BusStatisticsCollector>(),
            hilPanelStateStore: sp.GetRequiredService<PeakCan.Host.App.Services.HilPanel.HilPanelStateStore>(),
            // SecOC App 接线（2026-09-16 plan fix round 1）：显式工厂必须显式
            // 转发——MS DI 对工厂注册不回填未提供的可选参数，遗漏即生产静默裸跑。
            secOcVerdicts: sp.GetRequiredService<PeakCan.Host.Infrastructure.Channel.SecOc.SecOcVerdictTable>(),
            secOcPduProvider: sp.GetRequiredService<Func<ushort, IReadOnlyDictionary<uint, PeakCan.Host.Infrastructure.Channel.SecOc.SecOcPduConfig>?>>(),
            secOcBadgeJoiner: sp.GetRequiredService<PeakCan.Host.App.Services.SecOc.SecOcBadgeJoiner>(),
            // S3-T8（D1）：XCP 主 tab VM——显式工厂必须显式转发可选参（见上注）。
            xcpViewModel: sp.GetRequiredService<PeakCan.Host.App.ViewModels.Xcp.XcpViewModel>()));

        // === Flow G: Window + hosted services extracted to AppHostBuilder/WindowAndHostedServicesFlow.cs (W11 Task 6 — LAST extraction) ===
        RegisterWindowAndHostedServices(builder.Services);

        return builder.Build();
    }

}

/// <summary>
/// T7b 评审 L-2 修复：XCP 采集会话关闭宿主（S3-T8）。
/// <para>
/// IHostedService.StopAsync 在 App.RunShutdownAsync 的 host.StopAsync 内被
/// <b>真正 await</b>——保证 S2 quiesce 契约（停表 → 会话静默 → Dispose）在进程
/// 退出前完成，替代 VM Dispose 在 Dispatcher 上下文下的 fire-and-forget 兜底。
/// 注意：host stop 路径无 SyncContext，StopAsync 内部 ConfigureAwait(true) 的
/// 延续直接在线程池恢复，同步等待不会死锁（Dispatcher 上下文的死锁口径见
/// XcpAcquisitionPanelViewModel.Dispose 注释）。与 singleton 采集面板同实例
///（AppHostBuilderXcpTests 钉住）。
/// </para>
/// </summary>
internal sealed class XcpAcquisitionShutdownService : IHostedService
{
    private readonly PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel _acquisition;

    public XcpAcquisitionShutdownService(
        PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel acquisition) =>
        _acquisition = acquisition;

    /// <summary>关闭路径消费的采集面板（同 singleton 实例；测试断言口）。</summary>
    public PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel Acquisition => _acquisition;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // T8 评审 M2：关闭路径在线程池线程触碰 UI 绑定集合会炸——置静默开关，
        // quiesce（停表→静默→Dispose）本身与 UI 无关，跳过状态区刷新。
        _acquisition.SuppressStatusOutput = true;

        // T8 评审 M1：host.StopAsync 的 10s CTS 必须可执行——Task.WhenAny 包预算，
        // 超时记 Warning（会话卡在不可取消的 CAN 重试窗口时退出不再无限挂起）。
        var stopTask = _acquisition.StopAsync();
        var completed = await Task.WhenAny(
            stopTask,
            Task.Delay(ShutdownStopBudget, cancellationToken)).ConfigureAwait(false);
        if (completed != stopTask)
        {
            Serilog.Log.Warning(
                "XCP acquisition StopAsync exceeded {BudgetMs} ms shutdown budget — session may not be fully quiesced",
                ShutdownStopBudget.TotalMilliseconds);
            return;
        }

        // 关闭路径异常容忍（App.OnExit teardown 契约同款）：只保证 await 到位，
        // 不向 shutdown 传播异常。
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "XCP acquisition StopAsync failed during shutdown");
        }
    }

    /// <summary>关闭等待预算（与 host.StopAsync 的 10s 上限同量级，略短留余量）。</summary>
    internal static readonly TimeSpan ShutdownStopBudget = TimeSpan.FromSeconds(8);
}
