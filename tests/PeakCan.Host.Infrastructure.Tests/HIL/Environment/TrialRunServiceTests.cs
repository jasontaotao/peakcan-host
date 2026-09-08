using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Environment;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public sealed class TrialRunServiceTests
{
    [Fact]
    public async Task MissingLogicalChannel_ThrowsInvalidOperationException()
    {
        var suitePath = WriteSuite(true);
        var service = new TrialRunService(new RecordingRuntimeFactory().Create, NullLogger<TrialRunService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            suitePath,
            [new TrialChannelContext("bus-b", "USB2", new FakeChannel(), null)],
            default));
    }

    [Fact]
    public async Task EmptyEnvironment_ReturnsStructuredNoOp()
    {
        var suitePath = WriteSuite(false);
        var factory = new RecordingRuntimeFactory();
        var service = new TrialRunService(factory.Create, NullLogger<TrialRunService>.Instance);

        var result = await service.RunAsync(suitePath, [new TrialChannelContext("bus-a", "USB1", new FakeChannel(), null)], default);

        Assert.True(result.Passed);
        Assert.Single(result.Diagnostics);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task PreviewMode_ReturnsPreviewNotFullCheck()
    {
        var suitePath = WriteSuite(true);
        var factory = new RecordingRuntimeFactory();
        var service = new TrialRunService(factory.Create, NullLogger<TrialRunService>.Instance);

        var result = await service.RunAsync(suitePath, [new TrialChannelContext("bus-a", "USB1", new FakeChannel(), null)], default);

        Assert.True(result.Passed);
        Assert.False(result.IsFullHandshakeCheck);
        Assert.Single(result.Diagnostics);
        Assert.Single(factory.Created);
        Assert.Single(factory.Stopped);
    }

    [Fact]
    public async Task MultiChannel_GroupsNodesByDeclaredChannel()
    {
        var suitePath = WriteMultiChannelSuite();
        var factory = new RecordingRuntimeFactory();
        var service = new TrialRunService(factory.Create, NullLogger<TrialRunService>.Instance);

        var result = await service.RunAsync(suitePath,
            [
                new TrialChannelContext("bus-a", "USB1", new FakeChannel(), null),
                new TrialChannelContext("bus-b", "USB2", new FakeChannel(), null),
            ], default);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(2, factory.Stopped.Count);
    }

    private static string WriteSuite(bool withNode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"trial-{Guid.NewGuid():N}.suite.json");
        var node = withNode
            ? """
              ,{"name":"T","identity":{"kind":"rawCan"},"channel":"bus-a","trial":{"templateId":"tpl","handshake":[{"send":"CRM","thenReceive":"BRM","timeoutMs":50,"possibleCauses":["cause"]}],"requiredDbcMessages":[]}}
              """
            : "";
        var json = $$"""
            {
              "name":"TrialSuite",
              "cases":[],
              "globalCaseFixtureKeys":[],
              "suiteFixtureKeys":[],
              "config":{},
              "environment":[{{node.TrimStart(',')}}]
            }
            """;
        File.WriteAllText(path, json);
        return path;
    }

    private static string WriteMultiChannelSuite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trial-multi-{Guid.NewGuid():N}.suite.json");
        File.WriteAllText(path, """
        {
          "name":"TrialMulti",
          "cases":[],
          "globalCaseFixtureKeys":[],
          "suiteFixtureKeys":[],
          "config":{},
          "channels":[{"name":"bus-a"},{"name":"bus-b"}],
          "environment":[
            {"name":"A","identity":{"kind":"rawCan"},"channel":"bus-a","trial":{"templateId":"tpl","handshake":[{"send":"CRM","thenReceive":"BRM","timeoutMs":50,"possibleCauses":["cause"]}],"requiredDbcMessages":[]}},
            {"name":"B","identity":{"kind":"rawCan"},"channel":"bus-b","trial":{"templateId":"tpl","handshake":[{"send":"CRM","thenReceive":"BRM","timeoutMs":50,"possibleCauses":["cause"]}],"requiredDbcMessages":[]}}
          ]
        }
        """);
        return path;
    }

    private sealed class RecordingRuntimeFactory
    {
        public List<RecordingRuntime> Created { get; } = new();
        public List<RecordingRuntime> Stopped { get; } = new();

        public Func<ICanChannel, DbcDocument?, ITrialEnvironmentRuntime> Create => Invoke;

        public RecordingRuntime Invoke(ICanChannel channel, DbcDocument? dbc)
        {
            var runtime = new RecordingRuntime();
            Created.Add(runtime);
            runtime.Stopped += () => Stopped.Add(runtime);
            return runtime;
        }
    }

    private sealed class RecordingRuntime : ITrialEnvironmentRuntime
    {
        public event Action? Stopped;
        public void Start(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels) { }
        public void Stop() => Stopped?.Invoke();
        public IReadOnlyList<NodeRunStats> GetStats() => [];
    }
}
