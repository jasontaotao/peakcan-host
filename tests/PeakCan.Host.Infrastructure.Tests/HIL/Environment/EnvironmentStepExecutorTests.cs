using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class EnvironmentStepExecutorTests
{
    private sealed class TestBridge : IEnvironmentRuntimeBridge
    {
        public double? LastValue { get; set; }
        public byte[]? LastData { get; set; }
        public void SetSignalValue(string nodeName, string messageName, string signalName, double value)
            => LastValue = value;
        public void UpdateFrameData(string nodeName, MessageRef msgRef, byte[] data)
            => LastData = data;
    }

    [Fact]
    public void EnvironmentRuntime_ImplementsBridge()
    {
        var runtime = new EnvironmentRuntime(new FakeChannel());
        Assert.IsAssignableFrom<IEnvironmentRuntimeBridge>(runtime);
    }

    [Fact]
    public void SetSignalValue_UpdatesSignalState()
    {
        var dbc = DbcSignalsEncodingTests.CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeChannel(), null, dbc);
        runtime.Start([DbcSignalsEncodingTests.CreateDbcNode()], null);
        runtime.SetSignalValue("Charger", "CRM", "CRM_Signal", 99);
        var payload = runtime.GetEncodedPayload("Charger", "CRM");
        Assert.NotNull(payload);
        Assert.Equal(99, payload[0]);
        runtime.Stop();
    }
}