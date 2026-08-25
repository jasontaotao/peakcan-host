using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Polly;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL.Generators;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Assertions;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.Peak;
using PeakCan.Host.Infrastructure.Uds;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Builds the headless DI host for HIL test execution.
/// Supports both trace-replay mode (TraceDrivenChannel) and hardware mode (PeakCanChannel).
/// </summary>
public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        // Channel factory (hardware / trace / virtual-ECU / matrix)
        System.Diagnostics.Debug.WriteLine($"[Build] HardwareChannel={args.HardwareChannel}, HardwareChannels={(args.HardwareChannels is null ? "null" : args.HardwareChannels.Count.ToString())}, EcuScriptPath={args.EcuScriptPath}, MatrixPath={args.MatrixPath}, TracePath={args.TracePath}");
        if (args.HardwareChannels is { Count: > 0 } multiHw)
        {
            // Multi-channel hardware mode (2026-08-22, spec §3.4): the FIRST channel is
            // registered as the default ICanChannel singleton so single-channel-default
            // dependencies (BackgroundFrameSender / IFrameStatistics / IsoTpLayer / UdsClient
            // — UDS+stats multi-channel is deferred to Task 10/§3.4) resolve against the
            // default bus. MultiChannelAssertionContext (registered below) owns ALL channels
            // for per-step TargetChannel routing.
            var defaultHandle = ResolveChannelHandle(multiHw[0].Handle, index: 0);
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCanChannel>>();
                return new PeakCanChannel(new ChannelId(defaultHandle), logger);
            });
        }
        else if (args.HardwareChannel is not null)
        {
            // Hardware mode (Sprint 3) — single channel
            var handle = ParseChannelHandle(args.HardwareChannel);
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCanChannel>>();
                return new PeakCanChannel(new ChannelId(handle), logger);
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
            builder.Services.AddSingleton<ICanChannel>(channel);
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
            builder.Services.AddSingleton<ICanChannel>(_ => matrix.Channel);
        }
        else
        {
            // Trace-replay mode (Sprint 2)
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TraceDrivenChannel>>();
                var ch = new TraceDrivenChannel(new ChannelId(1), logger);
                ch.LoadAscii(args.TracePath);
                return ch;
            });
        }

        // DBC: DbcDocument 工厂单例 + IDbcLookup 依赖它（P0 修复 2026-08-10）。
        // 报告解码需要 DbcDocument.ValueTables（查 VAL_ 枚举文本），但 IDbcLookup 只暴露
        // FindMessage/GetAllMessages，无 ValueTables —— 故必须把 DbcDocument 本身注册进 DI。
        // 两个独立 lambda（MS DI 的 provider 构建后集合只读，不能在 lambda 内 AddSingleton）。
        builder.Services.AddSingleton(sp =>
        {
            var text = File.ReadAllText(args.DbcPath);
            var doc = PeakCan.HIL.Core.Dbc.DbcParser.Parse(text);
            if (!doc.IsSuccess)
                throw new InvalidOperationException($"DBC parse failed for '{args.DbcPath}': {doc.Error?.Message}");
            return doc.Value!;
        });
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>(sp =>
            new HeadlessDbcLookup(sp.GetRequiredService<DbcDocument>()));

        // Assertion context + UDS (hardware / virtual-ECU / matrix / trace)
        if (args.HardwareChannels is { Count: > 0 } multiCfg)
        {
            // Multi-channel hardware mode (2026-08-22, spec §3.4): build one
            // SingleChannelContext per ChannelConfig (own PeakCanChannel + own DBC +
            // own ChannelName), and register MultiChannelAssertionContext as
            // IAssertionContext. The default ICanChannel singleton (first channel) is
            // already registered above for single-channel-default deps (UDS/stats/bg).
            // UDS multi-channel is deferred (§3.4): IsoTpLayer/UdsClient bind to default.
            builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>();
                var peakLogger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanChannel>>();
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
                for (int i = 0; i < multiCfg.Count; i++)
                {
                    var cfg = multiCfg[i];
                    ICanChannel channel = i == 0
                        ? defaultChannel
                        : new PeakCanChannel(new ChannelId(ResolveChannelHandle(cfg.Handle, index: i)), peakLogger);
                    // Per-channel DBC (Q8: each channel = one network = one DBC).
                    DbcDocument dbcDoc;
                    if (i == 0 && cfg.DbcPath is null && globalDbc is not null)
                    {
                        // 首通道无独立 DbcPath → 复用全局 DbcDocument（与 args.DbcPath 同源）
                        dbcDoc = globalDbc;
                    }
                    else
                    {
                        var dbcPath = cfg.DbcPath ?? args.DbcPath;
                        var dbcText = File.ReadAllText(dbcPath);
                        var parsed = PeakCan.HIL.Core.Dbc.DbcParser.Parse(dbcText);
                        if (!parsed.IsSuccess)
                            throw new InvalidOperationException($"DBC parse failed for channel '{cfg.Name}' ('{dbcPath}'): {parsed.Error?.Message}");
                        dbcDoc = parsed.Value!;
                    }
                    var dbcLookup = new HeadlessDbcLookup(dbcDoc);
                    contexts[cfg.Name] = new SingleChannelContext(channel, dbcLookup, logger, channelName: cfg.Name);
                }
                return new MultiChannelAssertionContext(contexts, defaultChannelName: multiCfg[0].Name);
            });
            RegisterUdsServices(builder, args);
        }
        else if (args.HardwareChannel is not null)
        {
            // Hardware mode: PeakCanAssertionContext + ISO-TP bridge + UDS
            builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>();
                return new PeakCanAssertionContext(channel, dbc, logger);
            });
            RegisterUdsServices(builder, args);
        }
        else if (args.EcuScriptPath is not null || args.MatrixPath is not null)
        {
            // Virtual ECU / Matrix mode (Sprint 4/6): HILAssertionContext + UDS + VirtualEcu already registered
            builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<HILAssertionContext>>();
                return new HILAssertionContext(channel, dbc, args.EnableFaultInjection, logger);
            });
            RegisterUdsServices(builder, args);
        }
        else
        {
            // Trace-replay mode: HILAssertionContext (no UDS — trace is read-only)
            builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<HILAssertionContext>>();
                return new HILAssertionContext(channel, dbc, args.EnableFaultInjection, logger);
            });
        }

        // Fixture resolver (no-op for headless)
        builder.Services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

        // Assertion primitives (shared singleton)
        builder.Services.AddSingleton<AssertionPrimitives>();

        // Step executors (existing + Phase 3)
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, SendFrameStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, SendSequenceStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertSignalStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertRangeStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, WaitForSignalStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, DelayStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, ExpectFrameStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertResponseTimeStepExecutor>();
        // Phase 3: fault injection executors
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, InjectFaultStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, ClearFaultStepExecutor>();
        // Background frames: sender + step executor
        builder.Services.AddSingleton<BackgroundFrameSender>(sp =>
        {
            var channel = sp.GetRequiredService<ICanChannel>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<BackgroundFrameSender>>();
            return new BackgroundFrameSender(channel, logger);
        });
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, StepExecutor.ModifyBackgroundFrameStepExecutor>();
        // Phase A: Variables 断言（纯本地读 IStepVariableStore，不依赖 UDS → 所有模式可用，含 trace-replay）
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertDidValueStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertVariableStepExecutor>();
        // Phase B: 帧统计基础设施 + 时序断言（所有模式注册，含 trace-replay；依赖 IFrameStatistics 而非 IAssertionContext）
        // 多通道模式（spec §3.4，Task 10）：按通道独立 collector（各订阅自己 channel），
        // MultiChannelFrameStatistics 按 channelName 路由。单通道模式直接注册单 collector。
        builder.Services.AddSingleton<IFrameStatistics>(sp =>
        {
            if (args.HardwareChannels is { Count: > 0 } mcCfg
                && sp.GetService<PeakCan.HIL.Core.HIL.Contracts.IAssertionContext>() is MultiChannelAssertionContext multi)
            {
                var collectors = new Dictionary<string, FrameStatisticsCollector>(StringComparer.Ordinal);
                foreach (var name in multi.ChannelNames)
                    collectors[name] = new FrameStatisticsCollector(multi.GetChannel(name).Channel);
                return new MultiChannelFrameStatistics(collectors, defaultChannelName: mcCfg[0].Name);
            }
            var channel = sp.GetRequiredService<ICanChannel>();
            return new FrameStatisticsCollector(channel);
        });
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertNoFrameStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertFrameCountStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertCycleTimeStepExecutor>();

        // Engine
        // §3 dtcPresent 预查注入：IUdsSession 可选注入（trace-replay 模式未注册 → null → dtcPresent 不可用）
        builder.Services.AddSingleton<TestSuiteEngine>(sp => new TestSuiteEngine(
            sp.GetRequiredService<IFixtureResolver>(),
            sp.GetRequiredService<IEnumerable<IStepExecutor>>(),
            sp.GetService<IUdsSession>()));

        // Sprint 19 Inc 8: LLM failure analysis service with Polly retry.
        // Credential store for headless/CLI runs (env var / ~/.hil/credentials).
        builder.Services.AddSingleton<PeakCan.HIL.Core.Analysis.ICredentialStore,
            PeakCan.Host.Infrastructure.HIL.Analysis.SimpleCredentialStore>();
        // Phase 1: bind Llm config section (same as WPF AppHostBuilder).
        builder.Services.Configure<PeakCan.HIL.Core.Analysis.LlmOptions>(
            builder.Configuration.GetSection("Llm"));
        builder.Services.AddHttpClient<PeakCan.HIL.Core.HIL.Analysis.IHilAnalysisService,
            PeakCan.Host.Infrastructure.HIL.Analysis.HilAnalysisService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<PeakCan.HIL.Core.Analysis.LlmOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds * 5);
        })
        .AddPolicyHandler(GetRetryPolicy());

        // Logging
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("hil.log")
            .CreateLogger());

        return builder.Build();
    }

    /// <summary>
    /// Sprint 19 Inc 8: Polly retry policy for HilAnalysisService — retries
    /// transient HTTP errors and 429 rate limits up to 3 times with
    /// exponential backoff (1s → 2s → 4s).
    /// </summary>
    private static Polly.IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
        => Polly.Extensions.Http.HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

    /// <summary>
    /// Register ISO-TP + UDS services (shared between hardware and virtual ECU modes).
    /// </summary>
    private static void RegisterUdsServices(HostApplicationBuilder builder, CliArgs args)
    {
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
            return new UdsClient(isoTp);
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
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertDtcStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, AssertNrcStepExecutor>();
        // Phase A: UDS 结构化步骤 executors（依赖 UdsClient，仅 UDS 模式注册）
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, ReadDidStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, WriteDidStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, SessionControlStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, ClearDtcStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, RoutineControlStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, SecurityAccessStepExecutor>();
        // ODX Phase 0 (Task 0.2): ECUReset / CommunicationControl / IOControl executors
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, ECUResetStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, CommunicationControlStepExecutor>();
        builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, IOControlStepExecutor>();
    }

    /// <summary>
    /// 解析 ChannelConfig.Handle 为 PCAN-Basic 通道 handle。
    /// 接受两种形式（对齐 ChannelConfig 文档："raw hex 51 / C600"）：
    /// - raw hex（"51" / "0x51" / "C600"）→ 直接转 ushort；
    /// - "USB1".."USB16" 习惯形式 → ParseChannelHandle（0x51..0x60）。
    /// </summary>
    /// <param name="handle">通道 handle 串。</param>
    /// <param name="index">通道在 multiCfg 中的索引——空/非法 handle 时按
    /// 索引顺序映射 0x51+i（Spec v3 §3.4：studio 声明只留名，连接参数与硬件
    /// 口由 host 决定；非空 handle（旧套件）保持解析，向后兼容）。</param>
    public static ushort ResolveChannelHandle(string handle, int index = 0)
        => string.IsNullOrWhiteSpace(handle)
            ? (ushort)(0x51 + index)
            : ResolveChannelHandle(handle);

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
