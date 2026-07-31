using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Odx;
using PeakCan.Host.Infrastructure.Peak;

namespace PeakCan.Host.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var cli = CliArgsParser.Parse(args);

            // ODX import mode: no DI container needed
            if (cli.ImportOdxPath is not null)
            {
                var json = OdxEcuScriptImporter.ImportToJson(
                    cli.ImportOdxPath,
                    cli.ImportOdxEcuName ?? "ImportedECU",
                    cli.ImportOdxRequestId,
                    cli.ImportOdxResponseId);

                if (cli.OutputPath is not null)
                {
                    await File.WriteAllTextAsync(cli.OutputPath, json);
                    Console.WriteLine($"ECU script written to {cli.OutputPath}");
                }
                else
                {
                    Console.WriteLine(json);
                }
                return 0;
            }

            // Phase 5 Sprint 13: standalone ECU simulator mode
            if (cli.Simulate)
            {
                var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!);
                var handle = HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!);
                var channel = new PeakCanChannel(new ChannelId(handle), null);
                var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                Console.WriteLine($"Simulating ECU '{ecuScript.Name}' on {cli.HardwareChannel}. Press Ctrl+C to exit.");
                await host.RunAsync(cts.Token);
                return 0;
            }

            // Normal HIL test mode
            using var host2 = HeadlessHostBuilder.Build(cli);

            var engine = host2.Services.GetRequiredService<TestSuiteEngine>();
            var channel2 = host2.Services.GetRequiredService<ICanChannel>();
            var ctx = host2.Services.GetRequiredService<Core.HIL.Contracts.IAssertionContext>();

            var suiteJson = await File.ReadAllTextAsync(cli.SuitePath);
            var suite = JsonSerializer.Deserialize<TestSuite>(suiteJson, HILJsonOptions.Default);

            if (suite is null)
            {
                Console.Error.WriteLine("Error: failed to deserialize test suite JSON.");
                return 2;
            }

            await channel2.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
            try
            {
                var progress = cli.Format == "console" ? new ConsoleProgress() : null;
                var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(),
                    progress, default);

                if (cli.OutputPath is not null)
                {
                    if (cli.Format == "trx")
                        await ResultWriter.WriteTrx(result, cli.OutputPath);
                    else if (cli.Format == "junit")
                        await JUnitWriter.WriteJunit(result, cli.OutputPath);
                }

                return result.AllPassed ? 0 : 1;
            }
            finally
            {
                await channel2.DisconnectAsync();
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex.Message}");
            return 2;
        }
    }
}
