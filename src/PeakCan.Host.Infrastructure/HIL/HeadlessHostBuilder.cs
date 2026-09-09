using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Polly;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Host.Infrastructure.HIL.Generators;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.HIL.Environment;
using PeakCan.Host.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.Composite;
using PeakCan.Host.Infrastructure.Composition;
using PeakCan.Host.Infrastructure.Peak;
using PeakCan.Host.Infrastructure.Zlg;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Builds the headless DI host for HIL test execution.
/// Supports both trace-replay mode (TraceDrivenChannel) and hardware mode (PeakCanChannel).
/// </summary>
public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        // Channel factory (hardware / trace / virtual-ECU / matrix)
        System.Diagnostics.Debug.WriteLine($"[Build] HardwareChannel={args.HardwareChannel}, HardwareChannels={(args.HardwareChannels is null ? "null" : args.HardwareChannels.Count.ToString())}, EcuScriptPath={args.EcuScriptPath}, MatrixPath={args.MatrixPath}, TracePath={args.TracePath}");
        if (args.HardwareChannels is { Count: > 0 } multiHw)
        {
            // 多厂商通道工厂（产品 review: PEAK + ZLG + 未来厂商）。硬件模式注册
            // CompositeChannelFactory，按 ChannelId.Handle 范围分派 PeakCanChannel / ZlgCanChannel，
            // 取代硬编码 new PeakCanChannel——否则 ZLG handle 会错误地建 PeakCanChannel。
            RegisterChannelFactory(builder);
            // Multi-channel hardware mode (2026-08-22, spec §3.4): the FIRST channel is
            // registered as the default ICanChannel singleton so single-channel-default
            // dependencies (BackgroundFrameSender / IFrameStatistics / IsoTpLayer / UdsClient
            // — UDS+stats multi-channel is deferred to Task 10/§3.4) resolve against the
            // default bus. MultiChannelAssertionContext (registered below) owns ALL channels
            // for per-step TargetChannel routing.
            var defaultHandle = ResolveChannelHandle(multiHw[0].Handle, index: 0);
            // M2.4b-0（review NEW HIGH）：组装点上移到 DI ICanChannel 注册处（spec D1 单点）——
            // 默认通道在此完成 SecOC 组装，IsoTpLayer/HilIsoTpBridge/J1939/FrameStatistics
            // 等所有 DI 消费者与断言上下文看到同一包装视图，UDS 不再绕过 SecOcChannel。
            // 多通道 fault injection 保持原有未接线语义（enableFaultInjection:false）。
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var raw = sp.GetRequiredService<PeakCan.Host.Core.IChannelFactory>().Create(new ChannelId(defaultHandle));
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>();
                return ComposeChannel(raw, args, logger,
                    sp.GetService<Channel.SecOc.SecOcVerdictTable>(), sp.GetService<Channel.SecOc.SecOcStats>(),
                    enableFaultInjection: false);
            });
        }
        else if (args.HardwareChannel is not null)
        {
            // Hardware mode (Sprint 3) — single channel（也走工厂，支持单通道 ZLG）
            RegisterChannelFactory(builder);
            // ResolveChannelHandle 接受 USB{n}（PEAK）与 raw hex（如 "0xC600" ZLG），
            // 取代 ParseChannelHandle（仅 USB{n}）——否则单通道 ZLG 选不了。
            var handle = ResolveChannelHandle(args.HardwareChannel);
            // M2.4b-0：组装点上移（spec D1 单点）——SecOC + fault injection 在此组装，
            // UDS/ISO-TP/J1939/统计与断言上下文共用同一包装通道。
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var raw = sp.GetRequiredService<PeakCan.Host.Core.IChannelFactory>().Create(new ChannelId(handle));
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>();
                return ComposeChannel(raw, args, logger,
                    sp.GetService<Channel.SecOc.SecOcVerdictTable>(), sp.GetService<Channel.SecOc.SecOcStats>());
            });
        }
        else if (args.EcuScriptPath is not null)
        {
            // Virtual ECU mode (Sprint 4): VirtualChannel + VirtualEcu
            // Single VirtualChannel instance shared between VirtualEcu and HILAssertionContext
            // Phase 7 Unit B: external generator plugin directory (optional, LoadFromDirectory(null) = empty)
            var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir!);
            var ecuScript = EcuScriptLoader.Load(args.EcuScriptPath!, external);
            var channel = new CanChannels.VirtualChannel();
            // Eagerly create VirtualEcu (subscribes to channel.FrameReceived)
            var ecu = new StatefulVirtualEcu(channel, ecuScript.CanIds, ecuScript.StateMachine, logger: null);
            // Register as instances (not factories) to guarantee same object reference
            // M2.4b-0：DI 通道改为组装后注册（SecOC/faults 单点）；VirtualEcu 仍持有
            // 原始 channel 引用（ECU 模拟端不经过 SecOC，与真实硬件对端语义一致）。
            builder.Services.AddSingleton<ICanChannel>(sp => ComposeChannel(channel, args, null,
                sp.GetService<Channel.SecOc.SecOcVerdictTable>(), sp.GetService<Channel.SecOc.SecOcStats>()));
            builder.Services.AddSingleton(ecu);
        }
        else if (args.MatrixPath is not null)
        {
            // Multi-ECU matrix mode (Sprint 6): EcuMatrix with multiple VirtualEcu
            // Phase 7 Unit B: external generator plugin directory (L4/T3)
            var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir!);
            var config = MatrixConfigLoader.Load(args.MatrixPath!, external);
            var matrix = new EcuMatrix();
            foreach (var script in config.Ecus)
                matrix.AddEcu(script);
            builder.Services.AddSingleton(_ => matrix);
            // M2.4b-0：DI 通道改为组装后注册（spec D1 单点）；matrix 内部 ECU 仍直连原始通道。
            builder.Services.AddSingleton<ICanChannel>(sp => ComposeChannel(matrix.Channel, args, null,
                sp.GetService<Channel.SecOc.SecOcVerdictTable>(), sp.GetService<Channel.SecOc.SecOcStats>()));
        }
        else
        {
            // Trace-replay mode (Sprint 2): dispatch by file extension.
            // .blf → LoadBlf（BLF 二进制，含 bit31 扩展标记，parser 已掩码），
            // 其他 → LoadAscii（ASC 文本，双信号判扩展）。
            // 与 ReplayService/TraceViewerService 的分发约定一致。
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TraceDrivenChannel>>();
                var ch = new TraceDrivenChannel(new ChannelId(1), logger);
                if (args.TracePath!.EndsWith(".blf", StringComparison.OrdinalIgnoreCase))
                    ch.LoadBlf(args.TracePath);
                else
                    ch.LoadAscii(args.TracePath);
                // M2.4b-0：组装点上移（fault injection；trace 离线回放 SecOC 语义不变）
                return ComposeChannel(ch, args, logger,
                    sp.GetService<Channel.SecOc.SecOcVerdictTable>(), sp.GetService<Channel.SecOc.SecOcStats>());
            });
        }

        // DBC: DbcDocument 工厂单例 + IDbcLookup 依赖它（P0 修复 2026-08-10）。
        // 报告解码需要 DbcDocument.ValueTables（查 VAL_ 枚举文本），但 IDbcLookup 只暴露
        // FindMessage/GetAllMessages，无 ValueTables —— 故必须把 DbcDocument 本身注册进 DI。
        // 两个独立 lambda（MS DI 的 provider 构建后集合只读，不能在 lambda 内 AddSingleton）。
        // P0-3（2026-09-06）：解析走 DbcDocumentCache（path+mtime+size 缓存）——每次 run
        // 重建 host 不再重复读盘 + 解析同一 DBC（大 OEM DBC 解析可达数百 ms）。
        builder.Services.AddSingleton(sp => DbcDocumentCache.Load(args.DbcPath));
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>(sp =>
            new HeadlessDbcLookup(sp.GetRequiredService<DbcDocument>()));

        // Assertion context + UDS (hardware / virtual-ECU / matrix / trace)
        if (args.HardwareChannels is { Count: > 0 } multiCfg)
        {
            // Mutable per-channel DBC dictionary: populated inside the IAssertionContext factory
            // below (per-channel DbcDocument already loaded there), resolved by HilRunnerService
            // afterwards for multi-channel report generation. Empty for single-channel runs.
            var perChannelDbcs = new Dictionary<ChannelId, DbcDocument>();
            builder.Services.AddSingleton<IReadOnlyDictionary<ChannelId, DbcDocument>>(perChannelDbcs);

            // Task B 第二步（spec 2026-08-27 §2.2/§Q1）：per-channel UDS 会话字典（可变，factory 内填充）。
            // Channels[].UdsRequestId/UdsResponseId 非空的通道各获得独立 UDS 栈；resolver 的
            // 默认分支回落 RegisterUdsServices 注册的默认栈（CLI args，单通道零变化）。
            var udsSessions = new Dictionary<string, IUdsSession>(StringComparer.Ordinal);
            builder.Services.AddSingleton<IUdsSessionResolver>(sp => new UdsSessionResolver(
                udsSessions,
                () => sp.GetRequiredService<IUdsSession>()));

            // Multi-channel hardware mode (2026-08-22, spec §3.4): build one
            // SingleChannelContext per ChannelConfig (own PeakCanChannel + own DBC +
            // own ChannelName), and register MultiChannelAssertionContext as
            // IAssertionContext. The default ICanChannel singleton (first channel) is
            // already registered above for single-channel-default deps (UDS/stats/bg).
            // UDS multi-channel is deferred (§3.4): IsoTpLayer/UdsClient bind to default.
            builder.Services.AddSingleton<PeakCan.Host.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>();
                var contexts = new Dictionary<string, SingleChannelContext>(StringComparer.Ordinal);
                // 第一个通道复用 DI 注册的默认 ICanChannel singleton（同 handle，已在上面注册），
                // 避免对同一物理 handle new 第二个 PeakCanChannel（double-InitializeFD + 双读循环竞争）。
                // 其余通道各自 new PeakCanChannel（不同物理 handle，互不冲突）。
                var defaultChannel = sp.GetRequiredService<ICanChannel>();
                // Bug-C：全局 DbcDocument 已由上方 lambda 基于 args.DbcPath 解析一次（供
                // LastDbcDocument 报告 + 单通道 IDbcLookup）。多通道路径首通道 cfg.DbcPath
                // 通常 null → 回落 args.DbcPath → 与全局同源；复用全局实例避免重复 ReadAllText
                // + DbcParser.Parse。其余通道（DbcPath 非空或不同文件）各自解析。
                var globalDbc = sp.GetService<DbcDocument>();
                var factory = sp.GetRequiredService<PeakCan.Host.Core.IChannelFactory>();
                for (int i = 0; i < multiCfg.Count; i++)
                {
                    var cfg = multiCfg[i];
                    // 首通道复用 DI 默认 ICanChannel singleton（防同一 handle 双 InitializeFD/双读循环）；
                    // 其余通道按 handle 厂商分派（PEAK/ZLG，产品 review 多厂商）。
                    ICanChannel channel = i == 0
                        // 首通道复用 DI 默认 ICanChannel singleton（已在上方完成 SecOC 组装，
                        // 不得再 ComposeChannel——二次包装会被 ISecureChannel guard 拒绝）；
                        // 其余通道各自 new + 组装（不同物理 handle，互不冲突）。
                        ? defaultChannel
                        : ComposeChannel(
                            factory.Create(new ChannelId(ResolveChannelHandle(cfg.Handle, index: i))),
                            args, logger,
                            sp.GetService<Channel.SecOc.SecOcVerdictTable>(),
                            sp.GetService<Channel.SecOc.SecOcStats>(),
                            enableFaultInjection: false);
                    // Per-channel DBC (Q8: each channel = one network = one DBC).
                    DbcDocument dbcDoc;
                    if (i == 0 && cfg.DbcPath is null && globalDbc is not null)
                    {
                        // 首通道无独立 DbcPath → 复用全局 DbcDocument（与 args.DbcPath 同源）
                        dbcDoc = globalDbc;
                    }
                    else
                    {
                        // P0-3（2026-09-06）：per-channel DBC 也走缓存（多通道反复 run 同批 DBC）。
                        dbcDoc = DbcDocumentCache.Load(cfg.DbcPath ?? args.DbcPath);
                    }
                    var dbcLookup = new HeadlessDbcLookup(dbcDoc);
                    // Per-channel DBC for report: map ChannelId → DbcDocument
                    perChannelDbcs[channel.Id] = dbcDoc;
                    contexts[cfg.Name] = new SingleChannelContext(channel, dbcLookup, logger, channelName: cfg.Name);

                    // Task B 第二步（spec §2.2）：Channels[].UdsRequestId/UdsResponseId 非空 →
                    // 独立 UDS 栈（独立 IsoTp 过滤 ID + 独立安全访问锁状态机），绑定本通道 ICanChannel。
                    if (cfg.UdsRequestId is { } udsReqId && cfg.UdsResponseId is { } udsRespId)
                    {
                        var isoTp = new IsoTpLayer(new CanIdConfig
                        {
                            RequestId = udsReqId,
                            ResponseId = udsRespId,
                            IsExtendedFrame = false,
                        }, async frame => { await channel.WriteAsync(frame, default); });
                        // 入站桥接（review HIGH）：IsoTpLayer 不自动订阅 FrameReceived（同默认路径
                        // RegisterUdsServices 的 HilIsoTpBridge 语义）。bridge 订阅 channel 事件 →
                        // channel 持有 bridge 引用，不会被 GC；每通道独立桥接，互不串扰。
                        _ = new HilIsoTpBridge(channel, isoTp);
                        // 1.7.6：per-channel UDS 栈同样可挂 --key-dll 算法（未配则无算法 fail-fast）
                        var chanKeyAlgo = (PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm?)
                            sp.GetService<PeakCan.Host.Core.Uds.KeyDerivation.DllKeyDerivationAlgorithm>()
                            ?? sp.GetService<PeakCan.Host.Core.Uds.XorAAKeyDerivationAlgorithm>();
                        udsSessions[cfg.Name] = chanKeyAlgo is null
                            ? new UdsSessionAdapter(new UdsClient(isoTp))
                            : new UdsSessionAdapter(new UdsClient(isoTp, chanKeyAlgo));
                    }
                }
                return new MultiChannelAssertionContext(contexts, defaultChannelName: multiCfg[0].Name);
            });
            RegisterUdsServices(builder, args);
        }
        else if (args.HardwareChannel is not null)
        {
            // Hardware mode: PeakCanAssertionContext + ISO-TP bridge + UDS
            // M2.4b-0：DI ICanChannel 已组装（SecOC + faults），此处直接解析，防双包。
            builder.Services.AddSingleton<PeakCan.Host.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var dbc = sp.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>();
                return new PeakCanAssertionContext(sp.GetRequiredService<ICanChannel>(), dbc, logger);
            });
            RegisterUdsServices(builder, args);
        }
        else if (args.EcuScriptPath is not null || args.MatrixPath is not null)
        {
            // Virtual ECU / Matrix mode (Sprint 4/6): HILAssertionContext + UDS + VirtualEcu already registered
            // M2.4b-0：DI ICanChannel 已组装，直接解析，防双包。
            builder.Services.AddSingleton<PeakCan.Host.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var dbc = sp.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<HILAssertionContext>>();
                return new HILAssertionContext(sp.GetRequiredService<ICanChannel>(), dbc, logger,
                    sp.GetService<PeakCan.Host.Core.HIL.Contracts.ISecOcStats>());
            });
            RegisterUdsServices(builder, args);
        }
        else
        {
            // Trace-replay mode: HILAssertionContext (no UDS — trace is read-only)
            // M2.4b-0：DI ICanChannel 已组装，直接解析，防双包。
            builder.Services.AddSingleton<PeakCan.Host.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var dbc = sp.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<HILAssertionContext>>();
                return new HILAssertionContext(sp.GetRequiredService<ICanChannel>(), dbc, logger,
                    sp.GetService<PeakCan.Host.Core.HIL.Contracts.ISecOcStats>());
            });
        }

        // Fixture resolver (no-op for headless)
        builder.Services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

        // Assertion primitives (shared singleton)
        builder.Services.AddSingleton<AssertionPrimitives>();

        // Step executors (existing + Phase 3)
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, SendFrameStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, SendSequenceStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertSignalStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertRangeStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, WaitForSignalStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, DelayStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, ExpectFrameStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertResponseTimeStepExecutor>();
        // Phase 3: fault injection executors
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, InjectFaultStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, ClearFaultStepExecutor>();
        // Background frames: sender + step executor
        // Phase A: Variables 断言（纯本地读 IStepVariableStore，不依赖 UDS → 所有模式可用，含 trace-replay）
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertDidValueStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertVariableStepExecutor>();
        // Phase B: 帧统计基础设施 + 时序断言（所有模式注册，含 trace-replay；依赖 IFrameStatistics 而非 IAssertionContext）
        // 多通道模式（spec §3.4，Task 10）：按通道独立 collector（各订阅自己 channel），
        // MultiChannelFrameStatistics 按 channelName 路由。单通道模式直接注册单 collector。
        // SecOC 可观测性单例（spec §5-D6）：verdict 旁路表 + per-canId 统计，
        // ComposeChannel 与断言上下文共用同一实例（ISecOcStatsSource 能力下穿）。
        builder.Services.AddSingleton<Channel.SecOc.SecOcVerdictTable>();
        builder.Services.AddSingleton<Channel.SecOc.SecOcStats>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.Contracts.ISecOcStats>(sp =>
            sp.GetRequiredService<Channel.SecOc.SecOcStats>());
        builder.Services.AddSingleton<IFrameStatistics>(sp =>
        {
            if (args.HardwareChannels is { Count: > 0 } mcCfg
                && sp.GetService<PeakCan.Host.Core.HIL.Contracts.IAssertionContext>() is MultiChannelAssertionContext multi)
            {
                var collectors = new Dictionary<string, FrameStatisticsCollector>(StringComparer.Ordinal);
                foreach (var name in multi.ChannelNames)
                    collectors[name] = new FrameStatisticsCollector(multi.GetChannel(name).Channel);
                return new MultiChannelFrameStatistics(collectors, defaultChannelName: mcCfg[0].Name);
            }
            var channel = sp.GetRequiredService<ICanChannel>();
            return new FrameStatisticsCollector(channel);
        });
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertNoFrameStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertFrameCountStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertCycleTimeStepExecutor>();
        // Task C (spec 2026-08-27 §3): 信号维度时间窗断言——窗口收集解码帧快照，依赖 IAssertionContext 订阅（通道路由经 ctx）
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertSignalWithinStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertStableStepExecutor>();
        // J1939TP for EnvironmentRuntime: singleton wired to DI ICanChannel.
        // （2026-09-06：此处原先重复注册了两次相同的 J1939TpLayer 单例——MS DI 后注册
        // 覆盖前注册，行为一致但属死代码，已删除第二份。）
        builder.Services.AddSingleton<PeakCan.Host.Core.J1939.J1939TpLayer>(sp =>
        {
            var ch = sp.GetRequiredService<ICanChannel>();
            var jLogger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.Core.J1939.J1939TpLayer>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PeakCan.Host.Core.J1939.J1939TpLayer>.Instance;
            return new PeakCan.Host.Core.J1939.J1939TpLayer(
                (frame, ct) => ch.WriteAsync(frame, ct),
                new PeakCan.Host.Core.J1939.J1939TpOptions(), jLogger);
        });

        builder.Services.AddSingleton<Func<PeakCan.HIL.Core.HIL.StepExecutor.IEnvironmentRuntimeBridge?>>(sp => () => sp.GetRequiredService<EnvironmentRuntimeHolder>().Runtime);
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, SetEnvironmentSignalStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, ModifyEnvironmentFrameStepExecutor>();
        builder.Services.AddSingleton<EnvironmentRuntimeHolder>();

        // Engine
        // §3 dtcPresent 预查注入：IUdsSession 可选注入（trace-replay 模式未注册 → null → dtcPresent 不可用）
        builder.Services.AddSingleton<TestSuiteEngine>(sp => new TestSuiteEngine(
            sp.GetRequiredService<IFixtureResolver>(),
            sp.GetRequiredService<IEnumerable<IStepExecutor>>(),
            sp.GetService<IUdsSession>()));

        // Sprint 19 Inc 8: LLM failure analysis service with Polly retry.
        // P1-1（2026-09-06）：公共切片 LlmAnalysisComposition 单源（retry 策略 +
        // 超时算术此前在此与 AppHostBuilder 各手写一份，已漂移）。
        // Credential store for headless/CLI runs (env var / ~/.hil/credentials).
        builder.Services.AddLlmAnalysis(builder.Configuration);

        // Logging
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("hil.log")
            .CreateLogger());

        var host = builder.Build();
        // M3.4 devlog 遗留修复：HilIsoTpBridge 是懒注册单例，无人解析则 client isotp 的
        // ProcessFrame 永不接线（单通道/ECU 模式跑 UDS 步骤全超时）。注册了即急切实例化；
        // trace-replay 模式未注册 → GetService 返回 null，无副作用。多通道模式已自建 bridge。
        _ = host.Services.GetService<HilIsoTpBridge>();
        return host;
    }

    /// <summary>
    /// 注册多厂商通道工厂（产品 review: PEAK + ZLG + 未来厂商）。
    /// CompositeChannelFactory 按 ChannelId.Handle 范围分派：0x51-0x60 → PeakCanChannelFactory，
    /// 0x8000+ → ZlgCanChannelFactory。新增厂商 = 加一个 IChannelFactory 子类 + 注册进数组，
    /// 不改本 host 的通道分派逻辑。
    /// </summary>
    private static void RegisterChannelFactory(HostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<PeakCan.Host.Infrastructure.Peak.IPcanReader>(
            _ => new PeakCan.Host.Infrastructure.Peak.PcanReader());
        builder.Services.AddSingleton<IZlgReader>(_ => new ZlgReader());
        builder.Services.AddSingleton<ZlgDeviceManager>();
        builder.Services.AddSingleton<PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>();
        builder.Services.AddSingleton<ZlgCanChannelFactory>();
        builder.Services.AddSingleton<PeakCan.Host.Core.IChannelFactory>(sp => new CompositeChannelFactory(
        [
            sp.GetRequiredService<PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory>(),
            sp.GetRequiredService<ZlgCanChannelFactory>(),
        ]));
    }

    /// <summary>
    /// 注册 ISO-TP + UDS services (shared between hardware and virtual ECU modes).
    /// </summary>
    private static void RegisterUdsServices(HostApplicationBuilder builder, CliArgs args)
    {
        // backlog §9 1.7.6（2026-09-07）：--key-dll 提供时把 DllKeyDerivationAlgorithm 注册为
        // singleton（容器负责 Dispose native handle）；UdsClient 构造点经 GetService 可选取用。
        if (args.KeyDllPath is not null)
            builder.Services.AddSingleton<PeakCan.Host.Core.Uds.KeyDerivation.DllKeyDerivationAlgorithm>(
                _ => new PeakCan.Host.Core.Uds.KeyDerivation.DllKeyDerivationAlgorithm(args.KeyDllPath));
        // M3.4（spec Phase 3）：--key-algorithm builtin = 内置 XOR-0xAA（与虚拟 ECU
        // seed 算法一致）；与 --key-dll 互斥，DLL 优先（选取点按注册顺序 fallback）。
        else if (args.KeyAlgorithm == "builtin")
            builder.Services.AddSingleton<PeakCan.Host.Core.Uds.XorAAKeyDerivationAlgorithm>(
                new PeakCan.Host.Core.Uds.XorAAKeyDerivationAlgorithm());

        builder.Services.AddSingleton<IsoTpLayer>(sp =>
        {
            var config = new CanIdConfig
            {
                RequestId = args.UdsRequestId,
                ResponseId = args.UdsResponseId,
                IsExtendedFrame = false
            };
            var channel = sp.GetRequiredService<ICanChannel>();
            return new IsoTpLayer(config,
                async frame => { await channel.WriteAsync(frame, default).ConfigureAwait(false); });
        });
        builder.Services.AddSingleton<UdsClient>(sp =>
        {
            var isoTp = sp.GetRequiredService<IsoTpLayer>();
            // 1.7.6：--key-dll 未配时保持无算法 1 参构造（SecurityAccess fail-fast 不静默）
            // M3.4：DllKeyDerivationAlgorithm 优先，其次 --key-algorithm builtin（XOR-0xAA），
            // 两者皆空 = 1 参构造（占位 fail-fast）。
            var keyAlgo = (PeakCan.Host.Core.Uds.IKeyDerivationAlgorithm?)
                sp.GetService<PeakCan.Host.Core.Uds.KeyDerivation.DllKeyDerivationAlgorithm>()
                ?? sp.GetService<PeakCan.Host.Core.Uds.XorAAKeyDerivationAlgorithm>();
            return keyAlgo is null ? new UdsClient(isoTp) : new UdsClient(isoTp, keyAlgo);
        });
        builder.Services.AddSingleton<IUdsSession>(sp =>
        {
            var client = sp.GetRequiredService<UdsClient>();
            return new UdsSessionAdapter(client);
        });
        builder.Services.AddSingleton<HilIsoTpBridge>(sp =>
        {
            var channel = sp.GetRequiredService<ICanChannel>();
            var isoTp = sp.GetRequiredService<IsoTpLayer>();
            return new HilIsoTpBridge(channel, isoTp);
        });
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertDtcStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, AssertNrcStepExecutor>();
        // Phase A: UDS 结构化步骤 executors（依赖 UdsClient，仅 UDS 模式注册）
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, ReadDidStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, WriteDidStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, SessionControlStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, ClearDtcStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, RoutineControlStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, SecurityAccessStepExecutor>();
        // ODX Phase 0 (Task 0.2): ECUReset / CommunicationControl / IOControl executors
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, ECUResetStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, CommunicationControlStepExecutor>();
        builder.Services.AddSingleton<PeakCan.Host.Core.HIL.StepExecutor.IStepExecutor, IOControlStepExecutor>();

        // Task B 第二步（spec 2026-08-27 §Q1）：resolver 默认分支——非多通道模式（或通道未配
        // UDS ID）时 per-channel 字典为空，恒回落默认栈。TryAdd 避免覆盖多通道分支已注册的
        // resolver（后者带 per-channel 字典 + 同款默认 fallback）。
        builder.Services.TryAddSingleton<IUdsSessionResolver>(sp => new UdsSessionResolver(
            new Dictionary<string, IUdsSession>(StringComparer.Ordinal),
            () => sp.GetRequiredService<IUdsSession>()));
    }

    /// <summary>
/// Parse ChannelConfig.Handle into a PCAN-Basic channel handle.
/// Two forms (per ChannelConfig doc "raw hex 51 / C600"):
/// - raw hex ("51" / "0x51" / "C600") - direct ushort parse;
/// - "USB1".."USB16" convention - ParseChannelHandle (0x51..0x60).
/// Empty/blank handle maps by index to 0x51+index (Spec v3 §3.4: studio
/// declares names only; the physical port is host-side, ordered by index).
/// </summary>
    /// <summary>
    /// Single assembly point for headless channel decoration (spec §5-D1):
    /// fault injection + optional SecOC (from --secoc-config, D4 startup
    /// interception — a missing keyId fails this call loudly).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ICanChannel ComposeChannel(ICanChannel raw, CliArgs args,
        Microsoft.Extensions.Logging.ILogger? logger,
        Channel.SecOc.SecOcVerdictTable? verdictTable = null,
        Channel.SecOc.SecOcStats? stats = null,
        bool? enableFaultInjection = null)
    {
        var secocPdus = SecOcConfigLoader.LoadOptional(args.SecOcConfigPath,
            args.SecOcStoreDir, args.SecOcEntropy);
        return HilChannelComposer.Compose(raw, enableFaultInjection ?? args.EnableFaultInjection,
            secocPdus, verdictTable, stats, logger);
    }

    public static ushort ResolveChannelHandle(string handle, int index)
    => string.IsNullOrWhiteSpace(handle)
        ? (ushort)(0x51 + index)
        : ResolveChannelHandle(handle);

/// <summary>Parse a non-empty handle (hex or "USBn"); throws on invalid.</summary>
public static ushort ResolveChannelHandle(string handle)
{
    // raw hex（无前缀或 0x 前缀）
    var hex = handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? handle[2..] : handle;
    if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        return raw;
    // 回落到 "USBn" 形式
    return ParseChannelHandle(handle);
}

    /// <summary>
    /// 将 "USB1".."USB16" 字符串解析为 PCAN-Basic 通道 handle（0x51..0x60）。
    /// ⚠️ 仅支持 USB1..USB16；PCI/ISA/DNG 通道不在当前项目范围。
    /// </summary>
    public static ushort ParseChannelHandle(string hw)
    {
        if (hw.StartsWith("USB", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(hw[3..], out var n)
            && n is >= 1 and <= 16)
        {
            return (ushort)(0x50 + n);  // USB1 → 0x51, USB2 → 0x52, ...
        }
        throw new ArgumentException($"Invalid hardware channel: {hw}. Expected USB1..USB16.", nameof(hw));
    }
}
