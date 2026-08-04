using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.Channel.Gateway;
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

                // Phase 7 Unit D: --simulate + --gateway（长运行 ECU 模拟器桥接到 target 物理通道）。
                // target 生命周期本分支管理；--simulate 分支无 DI 容器，logger 保持 null（与 channel 创建一致）。
                CanBusGateway? simGateway = null;
                PeakCanChannel? simTargetChannel = null;
                if (cli.GatewayPath is not null)
                {
                    var config = GatewayConfigLoader.Load(cli.GatewayPath);
                    // L3: 自转发校验比较解析后 handle（--simulate 的 cli.HardwareChannel 非空，已由 validate 保证）。
                    if (HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!) ==
                        HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel))
                        throw new ArgumentException("Gateway source and target cannot be the same channel.");
                    simTargetChannel = new PeakCanChannel(new ChannelId(HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel)), null);
                    var simTargetConnect = await simTargetChannel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
                    // M2: target connect 硬件错误返回失败 Result —— 明确报错退出。
                    if (!simTargetConnect.IsSuccess)
                    {
                        Console.Error.WriteLine($"Error: Gateway target channel connect failed: {simTargetConnect.Error?.Message}");
                        return 2;
                    }
                    simGateway = new CanBusGateway(channel, simTargetChannel, config, null);
                    simGateway.Start();
                }

                // R3: host 放 try 外 —— using 逆序 dispose：gateway/target 在 finally 清理后 host 释放。
                using var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                Console.WriteLine($"Simulating ECU '{ecuScript.Name}' on {cli.HardwareChannel}. Press Ctrl+C to exit.");
                try
                {
                    await host.RunAsync(cts.Token);
                }
                finally
                {
                    if (simGateway is not null) await simGateway.DisposeAsync();
                    if (simTargetChannel is not null) await simTargetChannel.DisconnectAsync();
                }
                return 0;
            }

            // Normal HIL test mode
            using var host2 = HeadlessHostBuilder.Build(cli);

            var engine = host2.Services.GetRequiredService<TestSuiteEngine>();
            var channel2 = host2.Services.GetRequiredService<ICanChannel>();
            var ctx = host2.Services.GetRequiredService<PeakCan.HIL.Core.HIL.Contracts.IAssertionContext>();

            var suiteJson = await File.ReadAllTextAsync(cli.SuitePath);
            var suite = JsonSerializer.Deserialize<TestSuite>(suiteJson, HILJsonOptions.Default);

            if (suite is null)
            {
                Console.Error.WriteLine("Error: failed to deserialize test suite JSON.");
                return 2;
            }

            // Phase 7 Unit D: 总线间转发网关（可选，--hw/--ecu/--matrix + --gateway）。
            // H1 时序：target connect（source 之前）→ gateway.Start → source connect → engine → finally 8→9→10。
            CanBusGateway? gateway = null;
            PeakCanChannel? targetChannel = null;
            if (cli.GatewayPath is not null)
            {
                var config = GatewayConfigLoader.Load(cli.GatewayPath);
                // L3 自转发校验：比较解析后 handle（"USB01" 与 "USB1" 都解析 0x51，字符串比较会漏过别名）。
                if (cli.HardwareChannel is not null &&
                    HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel) ==
                    HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel))
                    throw new ArgumentException("Gateway source and target cannot be the same channel.");
                // E2: target channel 也传 ILogger（连接/写/读循环错误可观测）。
                var targetLogger = host2.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCanChannel>>();
                targetChannel = new PeakCanChannel(new ChannelId(HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel)), targetLogger);
                var targetConnect = await targetChannel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
                // M2: target connect 硬件错误返回失败 Result（不抛）—— 明确报错退出，避免网关静默失效。
                if (!targetConnect.IsSuccess)
                {
                    Console.Error.WriteLine($"Error: Gateway target channel connect failed: {targetConnect.Error?.Message}");
                    return 2;
                }
                var gwLogger = host2.Services.GetService<Microsoft.Extensions.Logging.ILogger<CanBusGateway>>();
                gateway = new CanBusGateway(channel2, targetChannel, config, gwLogger);
                gateway.Start();
            }

            try
            {
                // M1: source connect 移入 try —— connect 抛异常时 finally 仍清理 gateway/target（对齐 spec H1 异常路径）。
                await channel2.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
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
                // H1 dispose 顺序 8→9→10：先停网关退订 → 断开 target → 断开 source。
                if (gateway is not null) await gateway.DisposeAsync();
                if (targetChannel is not null) await targetChannel.DisconnectAsync();
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
