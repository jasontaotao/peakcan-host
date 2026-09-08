using System.Text.Json;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>试运行 runtime 生命周期抽象，便于编排测试。</summary>
public interface ITrialEnvironmentRuntime
{
    void Start(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels);
    void Stop();
    IReadOnlyList<NodeRunStats> GetStats();
}

/// <summary>真实 EnvironmentRuntime 适配器。</summary>
public sealed class TrialEnvironmentRuntime : ITrialEnvironmentRuntime
{
    private readonly EnvironmentRuntime _runtime;
    public TrialEnvironmentRuntime(ICanChannel channel, DbcDocument? dbc)
        => _runtime = new EnvironmentRuntime(channel, null, dbc);
    public void Start(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels)
        => _runtime.Start(nodes, channels);
    public void Stop() => _runtime.Stop();
    public IReadOnlyList<NodeRunStats> GetStats() => _runtime.GetStats();
}

/// <summary>按通道启动 EnvironmentRuntime 并执行 TrialRunner；绝不断开宿主连接。</summary>
public sealed class TrialRunService : ITrialRunService
{
    private readonly Func<ICanChannel, DbcDocument?, ITrialEnvironmentRuntime> _runtimeFactory;
    private readonly ILogger<TrialRunService> _logger;

    public TrialRunService(ILogger<TrialRunService> logger)
        : this((channel, dbc) => new TrialEnvironmentRuntime(channel, dbc), logger)
    {
    }

    public TrialRunService(
        Func<ICanChannel, DbcDocument?, ITrialEnvironmentRuntime> runtimeFactory,
        ILogger<TrialRunService> logger)
    {
        _runtimeFactory = runtimeFactory;
        _logger = logger;
    }

    public async Task<TrialRunResult> RunAsync(
        string suitePath,
        IReadOnlyList<TrialChannelContext> channels,
        CancellationToken ct = default)
    {
        var suiteJson = await File.ReadAllTextAsync(suitePath, ct);
        var suite = JsonSerializer.Deserialize<TestSuite>(suiteJson, HILJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize test suite JSON.");

        var nodes = suite.Environment ?? [];
        if (nodes.Count == 0)
        {
            return new TrialRunResult(true,
                [new TrialDiagnostic("Environment", true, "套件无环境节点，无可执行握手。", [])]);
        }

        var groups = new List<(TrialChannelContext Channel, IReadOnlyList<RestbusNode> Nodes)>();
        foreach (var group in nodes.GroupBy(n => n.Channel ?? (channels.Count == 1 ? channels[0].LogicalName : null)))
        {
            if (group.Key is null)
                throw new InvalidOperationException($"环境节点 '{group.First().Name}' 未绑定通道。");
            var context = channels.FirstOrDefault(c => c.LogicalName == group.Key)
                ?? throw new InvalidOperationException($"未找到逻辑通道 '{group.Key}' 的已连接通道。");
            groups.Add((context, group.ToList()));
        }

        var diagnostics = new List<TrialDiagnostic>();
        var passed = true;
        var fullCheck = true;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var runtime = _runtimeFactory(group.Channel.Channel, group.Channel.Dbc);
            try
            {
                runtime.Start(group.Nodes, suite.Channels);
                var runner = new TrialRunner(group.Channel.Channel)
                {
                    MessageIdLookup = BuildMessageIdLookup(group.Channel.Dbc),
                };
                var result = await runner.RunTrialAsync(group.Nodes, ct);
                diagnostics.AddRange(result.Diagnostics);
                passed &= result.Passed;
                fullCheck &= result.IsFullHandshakeCheck;
            }
            finally
            {
                runtime.Stop();
            }
        }

        return new TrialRunResult(passed, diagnostics, fullCheck);
    }

    private static Func<string, CanId?>? BuildMessageIdLookup(DbcDocument? dbc)
    {
        if (dbc is null) return null;
        return name =>
        {
            var message = dbc.Messages.FirstOrDefault(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (message is null) return null;
            return new CanId(
                message.Id & 0x1FFFFFFFu,
                (message.Id & 0x80000000u) == 0 ? FrameFormat.Standard : FrameFormat.Extended);
        };
    }
}
