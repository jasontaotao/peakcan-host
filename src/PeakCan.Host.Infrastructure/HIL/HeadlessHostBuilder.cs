using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Polly;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.HIL.Generators;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
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
        System.Diagnostics.Debug.WriteLine($"[Build] HardwareChannel={args.HardwareChannel}, EcuScriptPath={args.EcuScriptPath}, MatrixPath={args.MatrixPath}, TracePath={args.TracePath}");
        if (args.HardwareChannel is not null)
        {
            // Hardware mode (Sprint 3)
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

        // DBC lookup
        builder.Services.AddSingleton<Core.HIL.Contracts.IDbcLookup>(sp =>
        {
            var text = File.ReadAllText(args.DbcPath);
            var doc = Core.Dbc.DbcParser.Parse(text);
            if (!doc.IsSuccess)
                throw new InvalidOperationException($"DBC parse failed for '{args.DbcPath}': {doc.Error?.Message}");
            return new HeadlessDbcLookup(doc.Value!);
        });

        // Assertion context + UDS (hardware / virtual-ECU / matrix / trace)
        if (args.HardwareChannel is not null)
        {
            // Hardware mode: PeakCanAssertionContext + ISO-TP bridge + UDS
            builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
                return new PeakCanAssertionContext(channel, dbc);
            });
            RegisterUdsServices(builder, args);
        }
        else if (args.EcuScriptPath is not null || args.MatrixPath is not null)
        {
            // Virtual ECU / Matrix mode (Sprint 4/6): HILAssertionContext + UDS + VirtualEcu already registered
            builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<HILAssertionContext>>();
                return new HILAssertionContext(channel, dbc, args.EnableFaultInjection, logger);
            });
            RegisterUdsServices(builder, args);
        }
        else
        {
            // Trace-replay mode: HILAssertionContext (no UDS — trace is read-only)
            builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<HILAssertionContext>>();
                return new HILAssertionContext(channel, dbc, args.EnableFaultInjection, logger);
            });
        }

        // Fixture resolver (no-op for headless)
        builder.Services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

        // Assertion primitives (shared singleton)
        builder.Services.AddSingleton<AssertionPrimitives>();

        // Step executors (existing + Phase 3)
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, SendFrameStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, SendSequenceStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertSignalStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertRangeStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, WaitForSignalStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, DelayStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, ExpectFrameStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertResponseTimeStepExecutor>();
        // Phase 3: fault injection executors
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, InjectFaultStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, ClearFaultStepExecutor>();

        // Engine
        builder.Services.AddSingleton<TestSuiteEngine>();

        // Sprint 19 Inc 8: LLM failure analysis service with Polly retry.
        // Credential store for headless/CLI runs (env var / ~/.hil/credentials).
        builder.Services.AddSingleton<PeakCan.Host.Core.Analysis.ICredentialStore,
            PeakCan.Host.Infrastructure.HIL.Analysis.SimpleCredentialStore>();
        // Phase 7 Unit A: bind Llm:DeepSeek config section (same as WPF AppHostBuilder).
        builder.Services.Configure<PeakCan.Host.Core.Analysis.DeepSeekOptions>(
            builder.Configuration.GetSection("Llm:DeepSeek"));
        builder.Services.AddHttpClient<PeakCan.Host.Core.HIL.Analysis.IHilAnalysisService,
            PeakCan.Host.Infrastructure.HIL.Analysis.HilAnalysisService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<PeakCan.Host.Core.Analysis.DeepSeekOptions>>().Value;
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
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertDtcStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertNrcStepExecutor>();
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
