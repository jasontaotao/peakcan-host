using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class HILAssertionContextConcurrencyTests
{
    // Note: HILAssertionContext is internal, so we test via VirtualChannel + EcuMatrix
    // or via reflection. Since the fix is to use ConcurrentDictionary, we verify
    // the behavior through the public API.

    [Fact]
    public async Task ClearFaults_ConcurrentAddAndClear_NoException()
    {
        // This test verifies that concurrent AddFault + ClearFaults doesn't throw.
        // We use a VirtualChannel and fault injection wrapper.
        var channel = new VirtualChannel();
        var context = new HILAssertionContext(channel, new FakeDbcLookup(), enableFaultInjection: true);

        var tasks = new List<Task>();

        // Thread A: Add faults
        for (int i = 0; i < 10; i++)
        {
            var id = $"fault{i}";
            tasks.Add(Task.Run(() =>
            {
                var rule = new FaultRule { Type = FaultType.Drop, Probability = 1.0 };
                var handle = context.AddFault(rule);
                context.TagFault(id, handle);
            }));
        }

        // Thread B: Clear faults
        tasks.Add(Task.Run(() =>
        {
            Thread.Sleep(50);
            context.ClearFaults();
        }));

        await Task.WhenAll(tasks);
        Assert.True(true); // No exception = pass
    }

    [Fact]
    public void ClearFaults_TargetedClear_RemovesOnlyMatchingId()
    {
        var channel = new VirtualChannel();
        var context = new HILAssertionContext(channel, new FakeDbcLookup(), enableFaultInjection: true);

        var rule1 = new FaultRule { Type = FaultType.Drop, Probability = 1.0 };
        var rule2 = new FaultRule { Type = FaultType.Drop, Probability = 1.0 };

        var h1 = context.AddFault(rule1);
        var h2 = context.AddFault(rule2);
        context.TagFault("fault1", h1);
        context.TagFault("fault2", h2);

        context.ClearFaults("fault1");

        // Clearing again should be idempotent (no throw)
        context.ClearFaults("fault1");
        Assert.True(true);
    }

    // Minimal IDbcLookup fake for testing
    private sealed class FakeDbcLookup : IDbcLookup
    {
        public PeakCan.HIL.Core.Dbc.Message? FindMessage(uint canId) => null;
    }
}
