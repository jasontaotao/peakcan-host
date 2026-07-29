using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Cli;

/// <summary>
/// Builds the headless DI host for HIL test execution.
/// </summary>
public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        // Channel (TraceDrivenChannel loads ASC via LoadAscii)
        builder.Services.AddSingleton<Core.ICanChannel>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TraceDrivenChannel>>();
            var ch = new TraceDrivenChannel(new Core.ChannelId(1), logger);
            ch.LoadAscii(args.TracePath);
            return ch;
        });

        // DBC lookup
        builder.Services.AddSingleton<Core.HIL.Contracts.IDbcLookup>(sp =>
        {
            var text = File.ReadAllText(args.DbcPath);
            var doc = Core.Dbc.DbcParser.Parse(text);
            if (!doc.IsSuccess)
                throw new InvalidOperationException($"DBC parse failed: {doc.Error?.Message}");
            return new HeadlessDbcLookup(doc.Value!);
        });

        // Assertion context
        builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
        {
            var channel = sp.GetRequiredService<Core.ICanChannel>();
            var dbc = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
            return new HILAssertionContext(channel, dbc);
        });

        // Fixture resolver (no-op for headless)
        builder.Services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

        // Assertion primitives (shared singleton)
        builder.Services.AddSingleton<AssertionPrimitives>();

        // Step executors (6 existing internal classes)
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, SendFrameStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, SendSequenceStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertSignalStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertRangeStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, WaitForSignalStepExecutor>();
        builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, DelayStepExecutor>();

        // Engine
        builder.Services.AddSingleton<TestSuiteEngine>();

        // Logging
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("hil.log")
            .CreateLogger());

        return builder.Build();
    }
}
