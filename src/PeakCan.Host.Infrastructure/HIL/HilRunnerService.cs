using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Implementation of IHilRunnerService. Builds a scoped IHost per run and executes the test suite.
/// </summary>
public sealed class HilRunnerService : IHilRunnerService
{
    public async Task<TestSuiteResult> RunAsync(
        HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var host = HeadlessHostBuilder.Build(HilRunRequestExtensions.ToCliArgs(request));

        var engine = host.Services.GetRequiredService<TestSuiteEngine>();
        var channel = host.Services.GetRequiredService<ICanChannel>();
        var ctx = host.Services.GetRequiredService<IAssertionContext>();

        var suiteJson = await File.ReadAllTextAsync(request.SuitePath, ct);
        var suite = System.Text.Json.JsonSerializer.Deserialize<TestSuite>(suiteJson, Core.HIL.Serialization.HILJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize test suite JSON.");

        // 用例选择: 非空列表时只运行匹配的用例
        if (request.SelectedCaseNames is { Count: > 0 } selected)
            suite = suite with { Cases = suite.Cases.Where(c => selected.Contains(c.Name)).ToList() };

        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true, ct);
        try
        {
            return await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct);
        }
        finally
        {
            await channel.DisconnectAsync(ct);
        }
    }
}
