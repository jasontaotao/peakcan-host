using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var cli = CliArgsParser.Parse(args);
            using var host = HeadlessHostBuilder.Build(cli);

            var engine = host.Services.GetRequiredService<TestSuiteEngine>();
            var channel = host.Services.GetRequiredService<ICanChannel>();
            var ctx = host.Services.GetRequiredService<Core.HIL.Contracts.IAssertionContext>();

            var suiteJson = await File.ReadAllTextAsync(cli.SuitePath);
            var suite = JsonSerializer.Deserialize<TestSuite>(suiteJson, HILJsonOptions.Default);

            if (suite is null)
            {
                Console.Error.WriteLine("Error: failed to deserialize test suite JSON.");
                return 2;
            }

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            var progress = cli.Format == "console" ? new ConsoleProgress() : null;
            var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(),
                progress, default);

            await channel.DisconnectAsync();

            if (cli.OutputPath is not null && cli.Format == "trx")
            {
                await ResultWriter.WriteTrx(result, cli.OutputPath);
            }

            return result.AllPassed ? 0 : 1;
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
