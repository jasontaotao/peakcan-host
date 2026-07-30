using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
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

        // Channel factory (trace-replay or hardware)
        if (args.HardwareChannel is not null)
        {
            // Hardware mode
            var handle = ParseChannelHandle(args.HardwareChannel);
            builder.Services.AddSingleton<ICanChannel>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCanChannel>>();
                return new PeakCanChannel(new ChannelId(handle), logger);
            });
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
                throw new InvalidOperationException($"DBC parse failed: {doc.Error?.Message}");
            return new HeadlessDbcLookup(doc.Value!);
        });

        // Assertion context + UDS (hardware mode vs trace mode)
        if (args.HardwareChannel is not null)
        {
            // Hardware mode: PeakCanAssertionContext + ISO-TP bridge + UDS
            builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
                return new PeakCanAssertionContext(channel, dbc);
            });

            // ISO-TP layer + UDS client + adapter
            builder.Services.AddSingleton<IsoTpLayer>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var config = new CanIdConfig
                {
                    RequestId = args.UdsRequestId,
                    ResponseId = args.UdsResponseId,
                    IsExtendedFrame = false
                };
                return new IsoTpLayer(config, async frame => { await channel.WriteAsync(frame, default).ConfigureAwait(false); });
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
            // ISO-TP frame bridge: forwards FrameReceived → IsoTpLayer.ProcessFrame
            builder.Services.AddSingleton<HilIsoTpBridge>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var isoTp = sp.GetRequiredService<IsoTpLayer>();
                return new HilIsoTpBridge(channel, isoTp);
            });

            // UDS step executors
            builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertDtcStepExecutor>();
            builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertNrcStepExecutor>();
        }
        else
        {
            // Trace-replay mode: HILAssertionContext
            builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
            {
                var channel = sp.GetRequiredService<ICanChannel>();
                var dbc = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
                return new HILAssertionContext(channel, dbc, args.EnableFaultInjection);
            });
        }

        // Fixture resolver (no-op for headless)
        builder.Services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

        // Assertion primitives (shared singleton)
        builder.Services.AddSingleton<AssertionPrimitives>();

        // Step executors (6 existing + WaitForFrame)
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, SendFrameStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, SendSequenceStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertSignalStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertRangeStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, WaitForSignalStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, DelayStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, ExpectFrameStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertResponseTimeStepExecutor>();

        // Engine
        builder.Services.AddSingleton<TestSuiteEngine>();

        // Logging
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("hil.log")
            .CreateLogger());

        return builder.Build();
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
