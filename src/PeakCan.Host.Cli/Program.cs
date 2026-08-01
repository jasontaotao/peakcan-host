using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.Cli.Reporting;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Generators;
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
                // Phase 7 Unit B: hot-reloadable external generator plugins. manager
                // lives for the whole simulate run; ApplyTo wires built-in+external
                // merge and re-subscribes on every plugin directory change.
                using var manager = new GeneratorPluginManager(cli.GeneratorDir);
                var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!, manager.Current);
                manager.ApplyTo(ecuScript.StateMachine);
                var handle = HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!);
                var channel = new PeakCanChannel(new ChannelId(handle), null);
                using var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);

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

                // Report generation (after engine.ExecuteAsync).
                // console 模式下，engine.ExecuteAsync 运行期间已通过 ConsoleProgress 输出逐条进度（ProgressBar）；
                // 运行结束后追加 ConsoleSummaryFormatter 汇总。两者输出不冲突（进度是行内\r刷新，汇总是换行输出）。
                switch (cli.Format)
                {
                    case "html":
                        var trends = TrendTracker.Load("./hil-trends.json");
                        var html = HtmlReportGenerator.GenerateHtml(result, trends);
                        var htmlPath = cli.OutputPath ?? $"hil-report-{DateTime.UtcNow:yyyyMMddHHmmss}.html";
                        await File.WriteAllTextAsync(htmlPath, html);
                        Console.WriteLine($"HTML report written to {htmlPath}");
                        TrendTracker.Record(new TrendEntry(DateTime.UtcNow, result.SuiteName,
                            result.TotalCases, result.PassedCases, result.FailedCases, (int)result.ElapsedMs));
                        break;
                    case "html+junit":
                        var trends2 = TrendTracker.Load("./hil-trends.json");
                        var html2 = HtmlReportGenerator.GenerateHtml(result, trends2);
                        var htmlPath2 = Path.ChangeExtension(cli.OutputPath ?? "hil-report", ".html");
                        await File.WriteAllTextAsync(htmlPath2, html2);
                        var junitPath = Path.ChangeExtension(cli.OutputPath ?? "hil-report", ".xml");
                        await JUnitWriter.WriteJunit(result, junitPath);
                        TrendTracker.Record(new TrendEntry(DateTime.UtcNow, result.SuiteName,
                            result.TotalCases, result.PassedCases, result.FailedCases, (int)result.ElapsedMs));
                        break;
                    case "junit":
                        await JUnitWriter.WriteJunit(result, cli.OutputPath ?? "hil-report.xml");
                        break;
                    case "trx":
                        await ResultWriter.WriteTrx(result, cli.OutputPath ?? "hil-report.trx");
                        break;
                    case "console":
                    default:
                        Console.WriteLine(ConsoleSummaryFormatter.Format(result));
                        break;
                }

                // Frame export (independent of format)
                if (cli.ExportFramesDir is not null)
                {
                    await FrameCaptureExporter.ExportAsync(result, cli.ExportFramesDir);
                    Console.WriteLine($"Frame captures exported to {cli.ExportFramesDir}");
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
